using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using CoreIcons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace AllInOnePdf.UI.Services.Shared;

/// <summary>
/// Confirmation for actions that delete or reset something. Fluent's <c>ShowConfirmationAsync</c> draws a green
/// question mark (its "success" look), which reads as harmless; this one shows the warning icon and colour.
/// </summary>
public static class DestructiveConfirmation
{
    /// <summary>Asks before a destructive action. Returns true when the user chose <paramref name="confirmText"/>.</summary>
    public static async Task<bool> ConfirmDestructiveAsync(
        this IDialogService dialogs, string message, string confirmText, string title, string cancelText = "Cancel")
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        var dialog = await dialogs.ShowMessageBoxAsync(new DialogParameters<MessageBoxContent>
        {
            Content = new MessageBoxContent
            {
                Title = title,
                MarkupMessage = new MarkupString(WebUtility.HtmlEncode(message)),
                Icon = new CoreIcons.Regular.Size24.Warning(),
                IconColor = Color.Warning,
            },
            PrimaryAction = confirmText,
            SecondaryAction = cancelText,
        });
        var result = await dialog.Result;
        return !result.Cancelled;
    }
}
