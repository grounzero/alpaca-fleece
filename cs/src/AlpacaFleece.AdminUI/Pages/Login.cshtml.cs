using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AlpacaFleece.AdminUI.Pages;

/// <summary>
/// Handles admin login via a standard HTTP POST so that HttpContext.SignInAsync
/// is available — this is not possible inside the Blazor SignalR circuit.
/// </summary>
public sealed class LoginModel(AdminAuthService auth) : PageModel
{
    [BindProperty]
    public string Password { get; set; } = "";

    public string? Error { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!auth.Verify(Password))
        {
            Error = "Invalid password.";
            return Page();
        }

        var claims = new[] { new Claim(ClaimTypes.Name, "admin") };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal);

        var returnUrl = Request.Query["returnUrl"].FirstOrDefault() ?? "/";
        return LocalRedirect(returnUrl);
    }
}
