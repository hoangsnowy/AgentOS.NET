// GAP-19 — the desktop signs out by POSTing the antiforgery-protected form App.razor renders (#agentos-signout), not by
// navigating to a GET URL. One call for every Sign out / Lock button so the mechanism can't drift between them.

using System;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace AgentOs.Web.Auth;

/// <summary>Submits the sign-out form from an interactive component.</summary>
public static class AccountSignOut
{
    /// <summary>The JS function (shell-keys.js) that submits the form.</summary>
    public const string JsFunction = "agentosAccount.signOut";

    /// <summary>Sign the current user out (POST /account/logout with the page's antiforgery token).</summary>
    public static async Task SubmitAsync(IJSRuntime js)
    {
        ArgumentNullException.ThrowIfNull(js);
        await js.InvokeVoidAsync(JsFunction);
    }
}
