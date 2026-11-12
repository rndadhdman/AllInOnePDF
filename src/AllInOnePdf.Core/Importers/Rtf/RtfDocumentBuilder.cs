using System.Globalization;
using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.Core.Importers.Rtf;

/// <summary>
/// Reads the RTF token stream into the document model: a stack of group states (character and paragraph
/// formatting, current destination), the header tables (fonts, styles, lists, info), fields, pictures, footnotes
/// and tables. Paragraphs are collected as styled pieces and turned into blocks at <c>\par</c>
/// (see <c>RtfDocumentBuilder.Blocks.cs</c>).
/// </summary>
internal sealed partial class RtfDocumentBuilder
{
    /// <summary>Destinations whose whole group is left out, with or without <c>\*</c>.</summary>
    private static readonly HashSet<string> SkippedDestinations = new(StringComparer.Ordinal)
    {
        "colortbl", "filetbl", "revtbl", "protusertbl", "header", "headerl", "headerr", "headerf", "footer", "footerl",
        "footerr", "footerf", "xe", "tc", "tcn", "listtext", "nonesttables", "private", "rxe", "txe", "atnid",
        "atnauthor", "annotation", "atrfstart", "atrfend", "atndate", "atnref", "atnicn", "atnparent", "atntime",
        "comment", "ftnsep", "ftnsepc", "ftncn", "aftnsep", "aftnsepc", "aftncn", "datafield", "themedata",
        "colorschememapping", "latentstyles", "rsidtbl", "generator", "mmathPr", "xmlnstbl", "pgdsctbl", "userprops",
        "docvar", "template", "formfield", "fldtype", "listpicture", "wgrffmtfilter", "bkmkend", "objdata",
        "pnseclvl", "fchars", "lchars", "pgptbl", "passwordhash", "defchp", "defpap", "background", "datastore",
        "nesttableprops", "mmath", "keycode", "falt", "panose", "blipuid", "picprop", "shpinst", "result",
    };

    private readonly RtfTokenizer _tokenizer;
    private readonly Document _doc;
    private readonly RtfImportSettings _settings;
    private readonly ImportContext _context;
    private readonly CancellationToken _ct;

    private readonly List<RtfGroupState> _stack = [];
    private RtfGroupState _state = new(RtfCharState.Default, RtfParaState.Default, 1, RtfDestination.Content, RtfDestination.Content, 0, RtfCloser.None);
    private readonly List<RtfContext> _contexts = [new RtfContext()];

    private bool _atGroupStart;
    private bool _star;
    private int _unicodeSkip;

    private byte[] _bytes = new byte[256];
    private int _byteCount;

    // Header tables.
    private readonly Dictionary<int, RtfFont> _fonts = [];
    private readonly Dictionary<int, bool> _monoFonts = [];
    private readonly Dictionary<int, RtfStyle> _styles = [];
    private readonly Dictionary<int, (RtfStyleRole Role, int Level)> _roles = [];
    private readonly Dictionary<int, RtfListDefinition> _lists = [];
    private readonly Dictionary<int, int> _overrides = [];
    private int _defaultFont;
    private int _documentCodePage = RtfEncodings.Western;
    private readonly HashSet<int> _missingCodePages = [];

    // Destination scratch state.
    private RtfFont? _fontEntry;
    private readonly StringBuilder _fontName = new();
    private bool _fontNameDone;
    private RtfStyle? _styleEntry;
    private readonly StringBuilder _styleName = new();
    private bool _styleNameDone;
    private bool _styleIsParagraph = true;
    private RtfListDefinition? _list;
    private RtfListLevel? _listLevel;
    private int _overrideListId;
    private int _overrideLs;
    private string _infoKey = string.Empty;
    private readonly StringBuilder _infoText = new();
    private readonly int[] _creation = new int[5];
    private readonly Stack<RtfField> _fields = new();
    private readonly List<string> _links = [];
    private RtfPicture? _picture;
    private LegacyListKind _pnKind;
    private int _pnLevel;
    private int _pnStart = 1;
    private bool _pnContinue;
    private readonly StringBuilder _bookmarkText = new();

    // Counters for warnings.
    private int _skippedDrawings;
    private int _skippedObjects;
    private int _tooLargeImages;
    private int _footnoteCount;

    public RtfDocumentBuilder(byte[] data, int start, Document document, RtfImportSettings settings, ImportContext context, CancellationToken cancellationToken)
    {
        _tokenizer = new RtfTokenizer(data, start);
        _doc = document;
        _settings = settings;
        _context = context;
        _ct = cancellationToken;
    }

    private RtfContext Ctx => _contexts[^1];

    public void Build()
    {
        var count = 0;
        long nextReport = 65536;
        while (true)
        {
            var token = _tokenizer.Next();
            if ((++count & 0x3FF) == 0)
            {
                _ct.ThrowIfCancellationRequested();
                if (_tokenizer.Position >= nextReport)
                {
                    nextReport = _tokenizer.Position + 65536L;
                    _context.Report(Math.Min(0.95, _tokenizer.Position / (double)Math.Max(1, _tokenizer.Length)), "Reading RTF");
                }
            }

            if (token.Kind == RtfTokenKind.Eof || !Process(token))
            {
                break;
            }
        }

        FlushBytes();
        while (_stack.Count > 0)
        {
            CloseGroup();
        }

        Finish();
    }

    // ----------------------------------------------------------------- token dispatch

    /// <summary>Handles one token; false once the outermost group has closed.</summary>
    private bool Process(RtfToken token)
    {
        switch (token.Kind)
        {
            case RtfTokenKind.Text:
                Text(token.Start, token.Length);
                _atGroupStart = false;
                return true;
            case RtfTokenKind.HexByte:
                _atGroupStart = false;
                if (_unicodeSkip > 0)
                {
                    _unicodeSkip--;
                }
                else if (IsTextDestination(_state.Destination))
                {
                    AddBytes([token.Byte]);
                }

                return true;
            case RtfTokenKind.Binary:
                _atGroupStart = false;
                if (_unicodeSkip > 0)
                {
                    _unicodeSkip--;
                    return true;
                }

                FlushBytes();
                if (_state.Destination == RtfDestination.Picture && _picture is { } picture)
                {
                    WritePicture(picture, _tokenizer.Slice(token.Start, token.Length));
                }

                return true;
            case RtfTokenKind.GroupStart:
                FlushBytes();
                _unicodeSkip = 0;
                _stack.Add(_state);
                _state = _state with { Closer = RtfCloser.None };
                _atGroupStart = true;
                _star = false;
                if (_state.Destination == RtfDestination.StyleSheet && _styleEntry is null)
                {
                    _styleEntry = new RtfStyle();
                    _styleName.Clear();
                    _styleNameDone = false;
                    _styleIsParagraph = true;
                    _state = _state with { Closer = RtfCloser.StyleEntry };
                }

                return true;
            case RtfTokenKind.GroupEnd:
                _unicodeSkip = 0;
                _atGroupStart = false;
                return !CloseGroup();
            case RtfTokenKind.ControlSymbol:
                Symbol(token.Symbol);
                return true;
            case RtfTokenKind.ControlWord:
                FlushBytes();
                _unicodeSkip = 0;
                Word(token.Word, token.Parameter, token.HasParameter);
                return true;
            default:
                return true;
        }
    }

    private void Text(int start, int length)
    {
        if (_unicodeSkip > 0)
        {
            var k = Math.Min(_unicodeSkip, length);
            _unicodeSkip -= k;
            start += k;
            length -= k;
            if (length == 0)
            {
                return;
            }
        }

        var bytes = _tokenizer.Slice(start, length);
        if (_state.Destination == RtfDestination.Picture)
        {
            if (_picture is { } picture)
            {
                ReadHex(picture, bytes);
            }
        }
        else if (IsTextDestination(_state.Destination))
        {
            AddBytes(bytes);
        }
    }

    private void Symbol(char symbol)
    {
        if (symbol == '*')
        {
            _star = _atGroupStart;
            return;
        }

        _atGroupStart = false;
        if (_unicodeSkip > 0)
        {
            _unicodeSkip--;
            return;
        }

        switch (symbol)
        {
            case '{' or '}' or '\\':
                if (IsTextDestination(_state.Destination))
                {
                    AddBytes([(byte)symbol]);
                }

                break;
            case '~':
                FlushBytes();
                AppendText(" ");
                break;
            case '_':
                FlushBytes();
                AppendText("‑");
                break;
            case '\t':
                FlushBytes();
                AppendText("\t");
                break;
        }
    }

    private static bool IsTextDestination(RtfDestination d) => d is RtfDestination.Content or RtfDestination.FontTable
        or RtfDestination.StyleSheet or RtfDestination.InfoField or RtfDestination.FieldInstruction
        or RtfDestination.LegacyListText or RtfDestination.Bookmark;

    // ----------------------------------------------------------------- groups

    /// <summary>Pops the current group and runs what its destination needs at the end; true when the root closed.</summary>
    private bool CloseGroup()
    {
        FlushBytes();
        if (_stack.Count == 0)
        {
            return true;
        }

        var closed = _state;
        _state = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        RunCloser(closed);
        if (_stack.Count == 0)
        {
            EndParagraph(closed.Para, closed.Char);
            return true;
        }

        return false;
    }

    /// <summary>Consumes the rest of the current group without interpreting it.</summary>
    private void SkipGroup()
    {
        var depth = 1;
        var count = 0;
        while (depth > 0)
        {
            var token = _tokenizer.Next();
            if (token.Kind == RtfTokenKind.Eof)
            {
                break;
            }

            if ((++count & 0xFFF) == 0)
            {
                _ct.ThrowIfCancellationRequested();
            }

            if (token.Kind == RtfTokenKind.GroupStart)
            {
                depth++;
            }
            else if (token.Kind == RtfTokenKind.GroupEnd)
            {
                depth--;
            }
        }

        var closed = _state;
        if (_stack.Count > 1)
        {
            _state = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
        }

        if (closed.Closer == RtfCloser.StyleEntry)
        {
            _styleEntry = null;
        }

        _unicodeSkip = 0;
        _atGroupStart = false;
        _star = false;
    }

    private void SetDestination(RtfDestination destination, RtfCloser closer = RtfCloser.None) =>
        _state = _state with { Destination = destination, Closer = closer == RtfCloser.None ? _state.Closer : closer };

    private void RunCloser(RtfGroupState closed)
    {
        switch (closed.Closer)
        {
            case RtfCloser.FontTable:
                FinishFontEntry();
                break;
            case RtfCloser.StyleEntry:
                FinishStyleEntry();
                break;
            case RtfCloser.List:
                if (_list is { } list)
                {
                    _lists[list.Id] = list;
                }

                _list = null;
                break;
            case RtfCloser.ListLevel:
                if (_listLevel is { } level)
                {
                    _list?.Levels.Add(level);
                }

                _listLevel = null;
                break;
            case RtfCloser.ListOverride:
                if (_overrideLs > 0)
                {
                    _overrides[_overrideLs] = _overrideListId;
                }

                break;
            case RtfCloser.InfoField:
                AssignInfo();
                break;
            case RtfCloser.Field:
                if (_fields.Count > 0)
                {
                    _fields.Pop();
                }

                break;
            case RtfCloser.Picture:
                FinishPicture();
                break;
            case RtfCloser.Footnote:
                EndFootnote(closed);
                break;
            case RtfCloser.LegacyList:
                var para = _state.Para;
                _state = _state with
                {
                    Para = _pnContinue
                        ? para with { Legacy = LegacyListKind.None }
                        : para with { Legacy = _pnKind == LegacyListKind.None ? LegacyListKind.Bullet : _pnKind, LegacyLevel = _pnLevel, LegacyStart = _pnStart },
                };
                break;
            case RtfCloser.Bookmark:
                var name = _bookmarkText.ToString().Trim();
                if (name.Length > 0 && !name.Equals("_GoBack", StringComparison.OrdinalIgnoreCase))
                {
                    Ctx.Paragraph.Bookmarks.Add(name);
                }

                break;
        }
    }

    /// <summary>Enters a destination named by the first control word of a group; false when the word is not one.</summary>
    private bool TryDestination(string word)
    {
        var dest = _state.Destination;
        switch (word)
        {
            case "fonttbl":
                SetDestination(RtfDestination.FontTable, RtfCloser.FontTable);
                return true;
            case "stylesheet":
                SetDestination(RtfDestination.StyleSheet);
                return true;
            case "listtable":
                SetDestination(RtfDestination.ListTable);
                return true;
            case "listoverridetable":
                SetDestination(RtfDestination.ListOverrideTable);
                return true;
            case "info":
                SetDestination(RtfDestination.Info);
                return true;
            case "list" when dest == RtfDestination.ListTable:
                _list = new RtfListDefinition();
                SetDestination(dest, RtfCloser.List);
                return true;
            case "listlevel" when dest == RtfDestination.ListTable:
                _listLevel = new RtfListLevel();
                SetDestination(dest, RtfCloser.ListLevel);
                return true;
            case "listoverride" when dest == RtfDestination.ListOverrideTable:
                _overrideListId = 0;
                _overrideLs = 0;
                SetDestination(dest, RtfCloser.ListOverride);
                return true;
            case "title" or "subject" or "author" or "keywords" or "doccomm" when dest == RtfDestination.Info:
                _infoKey = word;
                _infoText.Clear();
                SetDestination(RtfDestination.InfoField, RtfCloser.InfoField);
                return true;
            case "creatim" when dest == RtfDestination.Info:
                _infoKey = word;
                Array.Clear(_creation);
                SetDestination(RtfDestination.CreationTime, RtfCloser.InfoField);
                return true;
            case "field" when dest == RtfDestination.Content:
                _fields.Push(new RtfField());
                SetDestination(dest, RtfCloser.Field);
                return true;
            case "fldinst":
                SetDestination(RtfDestination.FieldInstruction);
                return true;
            case "fldrslt" when dest == RtfDestination.Content:
                OpenFieldResult();
                return true;
            case "pict" when dest == RtfDestination.Content:
                _picture = new RtfPicture();
                SetDestination(RtfDestination.Picture, RtfCloser.Picture);
                return true;
            case "shppict":
                return true;
            case "nonshppict":
                SkipGroup();
                return true;
            case "object":
                _skippedObjects++;
                SkipGroup();
                return true;
            case "do":
                _skippedDrawings++;
                SkipGroup();
                return true;
            case "footnote" when dest == RtfDestination.Content:
                StartFootnote();
                return true;
            case "pntext" when dest == RtfDestination.Content:
                Ctx.Paragraph.LegacyMarker = true;
                Ctx.Paragraph.MarkerText.Clear();
                SetDestination(RtfDestination.LegacyListText);
                return true;
            case "pn":
                _pnKind = LegacyListKind.None;
                _pnLevel = 0;
                _pnStart = 1;
                _pnContinue = false;
                SetDestination(RtfDestination.LegacyList, RtfCloser.LegacyList);
                return true;
            case "upr":
                _state = _state with { SavedDestination = dest };
                SetDestination(RtfDestination.UnicodePair);
                return true;
            case "ud" when dest == RtfDestination.UnicodePair:
                SetDestination(_state.SavedDestination);
                return true;
            case "bkmkstart" when dest == RtfDestination.Content:
                _bookmarkText.Clear();
                SetDestination(RtfDestination.Bookmark, RtfCloser.Bookmark);
                return true;
        }

        if (SkippedDestinations.Contains(word) || dest == RtfDestination.Info
            || (dest == RtfDestination.UnicodePair && !string.Equals(word, "ud", StringComparison.Ordinal)))
        {
            SkipGroup();
            return true;
        }

        return false;
    }

    // ----------------------------------------------------------------- control words

    private void Word(string word, int p, bool hasP)
    {
        if (_atGroupStart)
        {
            var star = _star;
            _atGroupStart = false;
            _star = false;
            if (TryDestination(word))
            {
                return;
            }

            if (star)
            {
                SkipGroup();
                return;
            }
        }

        switch (word)
        {
            case "u":
                if (_state.Destination != RtfDestination.Picture)
                {
                    AppendText(((char)(p < 0 ? p + 65536 : p)).ToString());
                }

                _unicodeSkip = _state.UnicodeSkip;
                return;
            case "uc":
                _state = _state with { UnicodeSkip = Math.Max(0, p) };
                return;
        }

        switch (_state.Destination)
        {
            case RtfDestination.FontTable:
                FontTableWord(word, p, hasP);
                return;
            case RtfDestination.StyleSheet:
                StyleSheetWord(word, p);
                return;
            case RtfDestination.ListTable:
                ListTableWord(word, p);
                return;
            case RtfDestination.ListOverrideTable:
                if (word == "listid")
                {
                    _overrideListId = p;
                }
                else if (word == "ls")
                {
                    _overrideLs = p;
                }

                return;
            case RtfDestination.CreationTime:
                var index = word switch { "yr" => 0, "mo" => 1, "dy" => 2, "hr" => 3, "min" => 4, _ => -1 };
                if (index >= 0)
                {
                    _creation[index] = p;
                }

                return;
            case RtfDestination.Picture:
                PictureWord(word, p);
                return;
            case RtfDestination.LegacyList:
                LegacyListWord(word, p);
                return;
            case RtfDestination.Info or RtfDestination.UnicodePair or RtfDestination.Ignore:
                return;
        }

        if (CharacterWord(word, p, hasP))
        {
            return;
        }

        if (SpecialCharacter(word) is { } special)
        {
            AppendText(special);
            return;
        }

        if (_state.Destination == RtfDestination.Content)
        {
            ContentWord(word, p, hasP);
        }
    }

    private bool CharacterWord(string word, int p, bool hasP)
    {
        var on = !hasP || p != 0;
        var c = _state.Char;
        switch (word)
        {
            case "b":
                c = c with { Bold = on };
                break;
            case "i":
                c = c with { Italic = on };
                break;
            case "ul":
                c = c with { Underline = on };
                break;
            case "ulnone":
                c = c with { Underline = false };
                break;
            case "strike" or "striked":
                c = c with { Strike = on };
                break;
            case "super":
                c = c with { Superscript = on, Subscript = false };
                break;
            case "sub":
                c = c with { Subscript = on, Superscript = false };
                break;
            case "nosupersub":
                c = c with { Superscript = false, Subscript = false };
                break;
            case "scaps":
                c = c with { SmallCaps = on };
                break;
            case "highlight":
                c = c with { Highlight = hasP && p > 0 };
                break;
            case "v":
                c = c with { Hidden = on };
                break;
            case "f":
                c = c with { Font = p };
                break;
            case "fs":
                c = c with { FontSize = p > 0 ? p : 24 };
                break;
            case "plain":
                c = RtfCharState.Default;
                break;
            default:
                if (word.Length > 2 && word.StartsWith("ul", StringComparison.Ordinal) && word != "ulc")
                {
                    c = c with { Underline = on };
                    break;
                }

                return false;
        }

        _state = _state with { Char = c };
        return true;
    }

    private static string? SpecialCharacter(string word) => word switch
    {
        "tab" => "\t",
        "emdash" => "—",
        "endash" => "–",
        "lquote" => "‘",
        "rquote" => "’",
        "ldblquote" => "“",
        "rdblquote" => "”",
        "bullet" => "•",
        "emspace" or "enspace" or "qmspace" => " ",
        _ => null,
    };

    private void ContentWord(string word, int p, bool hasP)
    {
        var on = !hasP || p != 0;
        var para = _state.Para;
        switch (word)
        {
            case "par":
                EndParagraph(_state.Para, _state.Char);
                return;
            case "line":
                Ctx.Paragraph.Add(new RtfPiece { Kind = RtfPieceKind.Break, LinkId = _state.LinkId });
                return;
            case "page":
                PageBreak();
                return;
            case "sect":
                EndParagraph(_state.Para, _state.Char);
                _sectionPending = true;
                return;
            case "sectd" or "sbkpage" or "sbkeven" or "sbkodd":
                _sectionBreaksPage = true;
                return;
            case "sbknone" or "sbkcol":
                _sectionBreaksPage = false;
                return;
            case "cell":
                EndCell();
                return;
            case "nestcell":
                EndParagraph(_state.Para, _state.Char, forceCell: true);
                return;
            case "row":
                EndRow();
                return;
            case "trowd":
                (Ctx.Table ??= new RtfTableBuilder()).StartRowDefinition();
                return;
            case "trhdr":
                (Ctx.Table ??= new RtfTableBuilder()).MarkHeader();
                return;
            case "cellx":
                (Ctx.Table ??= new RtfTableBuilder()).CellBoundary(p);
                return;
            case "clmgf":
                (Ctx.Table ??= new RtfTableBuilder()).MergeFirst();
                return;
            case "clmrg":
                (Ctx.Table ??= new RtfTableBuilder()).MergeNext();
                return;
            case "clvmgf":
                (Ctx.Table ??= new RtfTableBuilder()).VerticalFirst();
                return;
            case "clvmrg":
                (Ctx.Table ??= new RtfTableBuilder()).VerticalNext();
                return;
            case "pard":
                para = RtfParaState.Default;
                break;
            case "ql":
                para = para with { Alignment = TextAlignment.Left };
                break;
            case "qc":
                para = para with { Alignment = TextAlignment.Center };
                break;
            case "qr":
                para = para with { Alignment = TextAlignment.Right };
                break;
            case "qj" or "qd":
                para = para with { Alignment = TextAlignment.Justify };
                break;
            case "outlinelevel":
                para = para with { OutlineLevel = p };
                break;
            case "s":
                para = para with { StyleId = p };
                break;
            case "intbl":
                para = para with { InTable = on };
                break;
            case "itap":
                para = para with { NestLevel = p };
                break;
            case "ilvl":
                para = para with { ListLevel = Math.Clamp(p, 0, 8) };
                break;
            case "ls":
                para = para with { ListOverride = p };
                break;
            case "li":
                para = para with { LeftIndent = p };
                break;
            case "fi":
                para = para with { FirstIndent = p };
                break;
            case "pagebb":
                para = para with { PageBreakBefore = on };
                break;
            case "deff":
                _defaultFont = p;
                return;
            case "ansi":
                _documentCodePage = RtfEncodings.Western;
                return;
            case "mac":
                _documentCodePage = 10000;
                return;
            case "pc":
                _documentCodePage = 437;
                return;
            case "pca":
                _documentCodePage = 850;
                return;
            case "ansicpg":
                if (p > 0)
                {
                    _documentCodePage = p;
                }

                return;
            default:
                return;
        }

        _state = _state with { Para = para };
    }

    // ----------------------------------------------------------------- header tables

    private void FontTableWord(string word, int p, bool hasP)
    {
        switch (word)
        {
            case "f":
                FinishFontEntry();
                _fontEntry = new RtfFont { Number = p };
                _fontName.Clear();
                _fontNameDone = false;
                break;
            case "fcharset" when _fontEntry is not null && hasP:
                _fontEntry.Charset = p;
                break;
            case "cpg" when _fontEntry is not null && p > 0:
                _fontEntry.CodePage = p;
                break;
            case "fmodern" when _fontEntry is not null:
                _fontEntry.Modern = true;
                break;
        }
    }

    private void FinishFontEntry()
    {
        if (_fontEntry is not { } font)
        {
            return;
        }

        font.Name = _fontName.ToString().Trim();
        _fonts[font.Number] = font;
        _monoFonts[font.Number] = font.IsMonospace;
        _fontEntry = null;
        _fontName.Clear();
    }

    private void StyleSheetWord(string word, int p)
    {
        if (_styleEntry is not { } style)
        {
            return;
        }

        switch (word)
        {
            case "s":
                style.Id = p;
                break;
            case "cs" or "ds" or "ts" or "tsrowd":
                _styleIsParagraph = false;
                break;
            case "outlinelevel":
                style.OutlineLevel = p;
                break;
            case "sbasedon":
                style.BasedOn = p;
                break;
            case "f":
                style.Font = p;
                break;
        }
    }

    private void FinishStyleEntry()
    {
        if (_styleEntry is { } style && _styleIsParagraph)
        {
            style.Name = _styleName.ToString().Trim();
            _styles[style.Id] = style;
        }

        _styleEntry = null;
    }

    private void ListTableWord(string word, int p)
    {
        switch (word)
        {
            case "listid" when _listLevel is null && _list is not null:
                _list.Id = p;
                break;
            case "levelnfc" or "levelnfcn" when _listLevel is not null:
                _listLevel.NumberFormat = p;
                break;
            case "levelstartat" when _listLevel is not null:
                _listLevel.StartAt = p;
                break;
        }
    }

    private void LegacyListWord(string word, int p)
    {
        switch (word)
        {
            case "pnlvlblt":
                _pnKind = LegacyListKind.Bullet;
                _pnLevel = 0;
                break;
            case "pnlvlbody":
                _pnKind = LegacyListKind.Numbered;
                _pnLevel = 0;
                break;
            case "pnlvl":
                _pnKind = LegacyListKind.Numbered;
                _pnLevel = Math.Clamp(p - 1, 0, 8);
                break;
            case "pnlvlcont":
                _pnContinue = true;
                break;
            case "pnstart":
                _pnStart = Math.Max(0, p);
                break;
            case "pndec" or "pnucltr" or "pnlcltr" or "pnucrm" or "pnlcrm" or "pnord" or "pncard" or "pnordt":
                if (_pnKind == LegacyListKind.None)
                {
                    _pnKind = LegacyListKind.Numbered;
                }

                break;
        }
    }

    private void AssignInfo()
    {
        var m = _doc.Metadata;
        if (_infoKey == "creatim")
        {
            try
            {
                if (_creation[0] > 0)
                {
                    var local = new DateTime(_creation[0], Math.Max(1, _creation[1]), Math.Max(1, _creation[2]), _creation[3], _creation[4], 0, DateTimeKind.Local);
                    m.Date = new DateTimeOffset(local);
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                // A damaged date is not worth failing over.
            }

            return;
        }

        var value = _infoText.ToString().Trim();
        if (value.Length == 0)
        {
            return;
        }

        switch (_infoKey)
        {
            case "title":
                m.Title ??= value;
                break;
            case "subject":
                m.Subtitle ??= value;
                break;
            case "doccomm":
                m.Description ??= value;
                break;
            case "author" when m.Authors.Count == 0:
                m.Authors.AddRange(value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                break;
            case "keywords" when m.Keywords.Count == 0:
                m.Keywords.AddRange(value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                break;
        }
    }

    // ----------------------------------------------------------------- text

    private void AddBytes(ReadOnlySpan<byte> bytes)
    {
        if (_byteCount + bytes.Length > _bytes.Length)
        {
            Array.Resize(ref _bytes, Math.Max(_bytes.Length * 2, _byteCount + bytes.Length));
        }

        bytes.CopyTo(_bytes.AsSpan(_byteCount));
        _byteCount += bytes.Length;
    }

    /// <summary>Decodes the pending text bytes together (so double-byte pairs survive) in the current font's code page.</summary>
    private void FlushBytes()
    {
        if (_byteCount == 0)
        {
            return;
        }

        var font = _state.Destination == RtfDestination.FontTable ? _fontEntry : FontOf(_state.Char.Font);
        var codePage = CodePageOf(font);
        var encoding = RtfEncodings.Get(codePage);
        if (encoding is null)
        {
            _missingCodePages.Add(codePage);
            encoding = RtfEncodings.WesternEncoding;
        }

        var text = encoding.GetString(_bytes, 0, _byteCount);
        _byteCount = 0;
        if (font is { IsSymbol: true } && _state.Destination is not RtfDestination.FontTable)
        {
            text = RtfEncodings.MapSymbolFont(text, font.IsWingdings);
        }

        AppendText(text);
    }

    private int CodePageOf(RtfFont? font)
    {
        if (font is not null)
        {
            if (font.Charset is { } charset && RtfEncodings.CodePageForCharset(charset) is { } fromCharset)
            {
                return fromCharset;
            }

            if (font.CodePage is { } cpg)
            {
                return cpg;
            }
        }

        return _documentCodePage;
    }

    private RtfFont? FontOf(int number) => _fonts.GetValueOrDefault(number < 0 ? _defaultFont : number);

    private bool IsMonospace(int number) => _monoFonts.GetValueOrDefault(number < 0 ? _defaultFont : number);

    /// <summary>Routes decoded text to the current destination.</summary>
    private void AppendText(string text)
    {
        switch (_state.Destination)
        {
            case RtfDestination.Content:
                if (_state.Char.Hidden)
                {
                    return;
                }

                var c = _state.Char;
                Ctx.Paragraph.AppendText(text, c.Style, IsMonospace(c.Font), _state.LinkId, c.FontSize);
                if (Ctx.IsBody)
                {
                    _sizeChars[c.FontSize] = _sizeChars.GetValueOrDefault(c.FontSize) + text.Length;
                }

                break;
            case RtfDestination.FontTable:
                AppendName(text, _fontName, ref _fontNameDone, FinishFontName);
                break;
            case RtfDestination.StyleSheet when _styleEntry is not null:
                AppendName(text, _styleName, ref _styleNameDone, null);
                break;
            case RtfDestination.InfoField:
                _infoText.Append(text);
                break;
            case RtfDestination.FieldInstruction:
                if (_fields.Count > 0)
                {
                    _fields.Peek().Instruction.Append(text);
                }

                break;
            case RtfDestination.LegacyListText:
                Ctx.Paragraph.MarkerText.Append(text);
                break;
            case RtfDestination.Bookmark:
                _bookmarkText.Append(text);
                break;
        }
    }

    private void FinishFontName()
    {
        if (_fontEntry is { } font)
        {
            font.Name = _fontName.ToString().Trim();
            _fonts[font.Number] = font;
            _monoFonts[font.Number] = font.IsMonospace;
        }
    }

    /// <summary>Table entries end their name with a semicolon.</summary>
    private static void AppendName(string text, StringBuilder name, ref bool done, Action? onDone)
    {
        foreach (var ch in text)
        {
            if (done)
            {
                return;
            }

            if (ch == ';')
            {
                done = true;
                onDone?.Invoke();
                return;
            }

            name.Append(ch);
        }
    }

    // ----------------------------------------------------------------- fields

    private void OpenFieldResult()
    {
        _state = _state with { Closer = RtfCloser.FieldResult };
        if (_fields.Count == 0 || ParseHyperlink(_fields.Peek().Instruction.ToString()) is not { } target)
        {
            return;
        }

        _links.Add(target);
        _state = _state with { LinkId = _links.Count };
    }

    /// <summary><c>HYPERLINK "url"</c>, <c>HYPERLINK \l "bookmark"</c> (→ <c>#bookmark</c>) or both; null for other fields.</summary>
    internal static string? ParseHyperlink(string instruction)
    {
        var args = SplitFieldArguments(instruction);
        if (args.Count == 0 || !args[0].Equals("HYPERLINK", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? url = null;
        string? anchor = null;
        for (var i = 1; i < args.Count; i++)
        {
            var a = args[i];
            if (a.Equals("\\l", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                anchor = args[++i];
            }
            else if ((a.Equals("\\o", StringComparison.OrdinalIgnoreCase) || a.Equals("\\t", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Count)
            {
                i++;
            }
            else if (!a.StartsWith('\\'))
            {
                url ??= a;
            }
        }

        if (!string.IsNullOrWhiteSpace(anchor))
        {
            return string.IsNullOrWhiteSpace(url) ? "#" + Rendering.DocumentHtmlRenderer.Slugify(anchor) : url + "#" + anchor;
        }

        return string.IsNullOrWhiteSpace(url) ? null : url.Trim();
    }

    private static List<string> SplitFieldArguments(string text)
    {
        var args = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        var has = false;
        foreach (var ch in text)
        {
            if (ch == '"')
            {
                if (quoted)
                {
                    args.Add(sb.ToString());
                    sb.Clear();
                    has = false;
                }
                else if (has)
                {
                    args.Add(sb.ToString());
                    sb.Clear();
                    has = false;
                }

                quoted = !quoted;
            }
            else if (!quoted && char.IsWhiteSpace(ch))
            {
                if (has)
                {
                    args.Add(sb.ToString());
                    sb.Clear();
                    has = false;
                }
            }
            else
            {
                sb.Append(ch);
                has = true;
            }
        }

        if (has)
        {
            args.Add(sb.ToString());
        }

        return args;
    }

    // ----------------------------------------------------------------- pictures

    private void PictureWord(string word, int p)
    {
        if (_picture is not { } picture)
        {
            return;
        }

        switch (word)
        {
            case "pngblip":
                picture.MediaType = "image/png";
                break;
            case "jpegblip":
                picture.MediaType = "image/jpeg";
                break;
            case "emfblip" or "wmetafile" or "macpict" or "pmmetafile" or "dibitmap" or "wbitmap":
                picture.Drawing = true;
                break;
            case "picw":
                picture.PixelWidth = p;
                break;
            case "pich":
                picture.PixelHeight = p;
                break;
            case "picwgoal":
                picture.GoalWidth = p;
                break;
            case "pichgoal":
                picture.GoalHeight = p;
                break;
            case "picscalex":
                picture.ScaleX = p > 0 ? p : 100;
                break;
        }
    }

    private void ReadHex(RtfPicture picture, ReadOnlySpan<byte> text)
    {
        if (picture.TooLarge || picture.Drawing)
        {
            return;
        }

        foreach (var b in text)
        {
            var v = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - '0',
                >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
                _ => -1,
            };
            if (v < 0)
            {
                continue;
            }

            if (picture.PendingNibble < 0)
            {
                picture.PendingNibble = v;
            }
            else
            {
                picture.Data.WriteByte((byte)((picture.PendingNibble << 4) | v));
                picture.PendingNibble = -1;
            }
        }

        if (picture.Data.Length > _settings.MaxImageBytes)
        {
            picture.TooLarge = true;
            picture.Data.SetLength(0);
        }
    }

    private void WritePicture(RtfPicture picture, ReadOnlySpan<byte> data)
    {
        if (picture.TooLarge || picture.Drawing)
        {
            return;
        }

        picture.Data.Write(data);
        if (picture.Data.Length > _settings.MaxImageBytes)
        {
            picture.TooLarge = true;
            picture.Data.SetLength(0);
        }
    }

    private void FinishPicture()
    {
        if (_picture is not { } picture)
        {
            return;
        }

        _picture = null;
        using (picture.Data)
        {
            if (picture.Drawing || picture.MediaType is null)
            {
                _skippedDrawings++;
                return;
            }

            if (!_settings.ExtractImages)
            {
                return;
            }

            if (picture.TooLarge)
            {
                _tooLargeImages++;
                return;
            }

            var bytes = picture.Data.ToArray();
            if (bytes.Length == 0)
            {
                return;
            }

            var resource = _doc.AddResource(picture.MediaType, bytes);
            var image = new ImageBlock { Source = resource.Reference };
            if (PngSize(bytes) is { } size)
            {
                image.PixelWidth = size.Width;
                image.PixelHeight = size.Height;
            }
            else if (picture.PixelWidth > 0 && picture.PixelHeight > 0)
            {
                image.PixelWidth = picture.PixelWidth;
                image.PixelHeight = picture.PixelHeight;
            }

            if (picture.GoalWidth > 0)
            {
                var points = picture.GoalWidth * (picture.ScaleX / 100.0) / 20.0;
                image.WidthPercent = Math.Round(Math.Min(100, points / ContentWidthPoints * 100), 1);
            }

            Ctx.Paragraph.Add(new RtfPiece { Kind = RtfPieceKind.Image, Image = image, LinkId = _state.LinkId });
        }
    }

    /// <summary>6.5 in: the text width of a Letter/A4 page with 1 in margins, the reference for picture widths.</summary>
    private const double ContentWidthPoints = 468;

    private static (int Width, int Height)? PngSize(byte[] b)
    {
        if (b.Length < 24 || b[0] != 0x89 || b[1] != 'P' || b[2] != 'N' || b[3] != 'G')
        {
            return null;
        }

        var w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
        var h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
        return w > 0 && h > 0 ? (w, h) : null;
    }

    // ----------------------------------------------------------------- footnotes

    private void StartFootnote()
    {
        if (!_settings.Footnotes)
        {
            SkipGroup();
            return;
        }

        var label = (++_footnoteCount).ToString(CultureInfo.InvariantCulture);
        Ctx.Paragraph.Add(new RtfPiece { Kind = RtfPieceKind.FootnoteReference, Label = label, LinkId = _state.LinkId });
        _contexts.Add(new RtfContext { Note = new Footnote { Label = label } });
        _state = _state with { Closer = RtfCloser.Footnote, Para = RtfParaState.Default, LinkId = 0 };
    }

    private void EndFootnote(RtfGroupState closed)
    {
        if (_contexts.Count < 2)
        {
            return;
        }

        var ctx = Ctx;
        EndParagraph(closed.Para, closed.Char);
        if (ctx.Table is not null)
        {
            FinishTable(ctx);
        }

        _contexts.RemoveAt(_contexts.Count - 1);
        ctx.Note!.Blocks.AddRange(ctx.Sink.Blocks);
        _doc.Footnotes.Add(ctx.Note);
    }
}
