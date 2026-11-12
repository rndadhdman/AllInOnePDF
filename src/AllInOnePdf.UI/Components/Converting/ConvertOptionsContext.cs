using AllInOnePdf.UI.Services;

namespace AllInOnePdf.UI.Components.Converting;

/// <summary>Data handed to <see cref="ConvertOptionsDrawer"/> through <c>IDialogService.ShowPanelAsync</c>.</summary>
public sealed record ConvertOptionsContext(ConvertItem Item, ConvertWorkspace Workspace, bool StartWithInput = false);
