﻿using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VueCoreFramework.Models;
using VueCoreFramework.Models.ViewModels;
using VueCoreFramework.Models.ViewModels.AccountViewModels;
using VueCoreFramework.Services;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.Extensions.Localization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Http;

namespace VueCoreFramework.Controllers
{
    /// <summary>
    /// An MVC controller for handling user account tasks.
    /// </summary>
    [Route("api/[controller]/[action]")]
    public class AccountController : Controller
    {
        private readonly AdminOptions _adminOptions;
        private readonly IEmailSender _emailSender;
        private readonly IStringLocalizer<ErrorMessages> _errorLocalizer;
        private readonly ILogger<AccountController> _logger;
        private readonly IStringLocalizer<EmailMessages> _emailLocalizer;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly TokenProviderOptions _tokenOptions;
        private readonly UserManager<ApplicationUser> _userManager;

        /// <summary>
        /// Initializes a new instance of <see cref="AccountController"/>.
        /// </summary>
        public AccountController(
            IOptions<AdminOptions> adminOptions,
            IEmailSender emailSender,
            IStringLocalizer<ErrorMessages> errorLocalizer,
            ILogger<AccountController> logger,
            IStringLocalizer<EmailMessages> emailLocalizer,
            SignInManager<ApplicationUser> signInManager,
            IOptions<TokenProviderOptions> tokenOptions,
            UserManager<ApplicationUser> userManager)
        {
            _adminOptions = adminOptions.Value;
            _emailSender = emailSender;
            _errorLocalizer = errorLocalizer;
            _logger = logger;
            _emailLocalizer = emailLocalizer;
            _signInManager = signInManager;
            _tokenOptions = tokenOptions.Value;
            _userManager = userManager;
        }

        /// <summary>
        /// The endpoint reached when a user clicks a link in an email generated by the <see
        /// cref="ManageController"/>'s ChangeEmail action.
        /// </summary>
        /// <returns>
        /// Redirect to an error page in the event of a bad request; or to a success page if successful.
        /// </returns>
        [HttpGet]
        public async Task<IActionResult> ChangeEmail(string userId, string code)
        {
            if (userId == null || code == null)
            {
                return RedirectToAction(nameof(HomeController.Index), new { forwardUrl = "/error/400" });
            }
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null || user.NewEmail == null)
            {
                return RedirectToAction(nameof(HomeController.Index), new { forwardUrl = "/error/400" });
            }
            var old = user.Email;
            var result = await _userManager.ChangeEmailAsync(user, user.NewEmail, code);
            if (result.Succeeded)
            {
                _logger.LogInformation(LogEvent.EMAIL_CHANGE_CONFIRM, "Email change confirmed from {OLDEMAIL} to {NEWEMAIL}.", user.Email, user.NewEmail);
                user.OldEmail = old;
                user.NewEmail = null;
                await _userManager.UpdateAsync(user);
            }
            return RedirectToAction(nameof(HomeController.Index), new { forwardUrl = result.Succeeded ? "/user/email/confirm" : "/error/400" });
        }

        /// <summary>
        /// The endpoint reached when a user clicks a link in an email generated by various actions
        /// which require confirmation of an email address.
        /// </summary>
        /// <returns>
        /// Redirect to an error page in the event of a bad request; or to a success page if successful.
        /// </returns>
        [HttpGet]
        public async Task<IActionResult> ConfirmEmail(string userId, string code)
        {
            if (userId == null || code == null)
            {
                return RedirectToAction(nameof(HomeController.Index), new { forwardUrl = "/error/400" });
            }
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                return RedirectToAction(nameof(HomeController.Index), new { forwardUrl = "/error/400" });
            }
            var result = await _userManager.ConfirmEmailAsync(user, code);
            if (result.Succeeded)
            {
                _logger.LogInformation(LogEvent.EMAIL_CONFIRM, "Email {EMAIL} confirmed.", user.Email);
            }
            return RedirectToAction(nameof(HomeController.Index), new { forwardUrl = result.Succeeded ? "/user/email/confirm" : "/error/400" });
        }

        /// <summary>
        /// Called to log in with an external authentication provider account.
        /// </summary>
        /// <param name="model">A <see cref="LoginViewModel"/> used to transfer task data.</param>
        /// <returns>
        /// A <see cref="LoginViewModel"/> used to transfer task data in the event of a problem,
        /// or a <see cref="ChallengeResult"/> for the authentication provider.
        /// </returns>
        /// <response code="400">There was a problem authenticating with that provider.</response>
        /// <response code="302">Redirect to external authentication provider.</response>
        [HttpPost]
        [ProducesResponseType(typeof(IDictionary<string, string>), 400)]
        [ProducesResponseType(302)]
        public IActionResult ExternalLogin([FromBody]LoginViewModel model)
        {
            var provider = _signInManager.GetExternalAuthenticationSchemes().SingleOrDefault(a => a.DisplayName == model.AuthProvider);
            if (provider == null)
            {
                _logger.LogWarning(LogEvent.EXTERNAL_PROVIDER_NOTFOUND, "Could not find provider {PROVIDER}.", model.AuthProvider);
                return BadRequest(_errorLocalizer[ErrorMessages.AuthProviderError]);
            }
            // Request a redirect to the external login provider.
            var redirectUrl = Url.Action(nameof(ExternalLoginCallback), "Account", new { ReturnUrl = model.ReturnUrl, RememberUser = model.RememberUser });
            var properties = _signInManager.ConfigureExternalAuthenticationProperties(provider.AuthenticationScheme, redirectUrl);
            return Challenge(properties, provider.AuthenticationScheme);
        }

        /// <summary>
        /// The endpoint reached when a user returns from an external authentication provider when
        /// attempting to sign in with that account.
        /// </summary>
        /// <returns>A <see cref="LoginViewModel"/> used to transfer task data.</returns>
        /// <response code="400">Invalid login attempt.</response>
        /// <response code="200">External login response data.</response>
        [HttpGet]
        [ProducesResponseType(typeof(IDictionary<string, string>), 400)]
        [ProducesResponseType(typeof(LoginViewModel), 200)]
        public async Task<IActionResult> ExternalLoginCallback(string returnUrl = null, bool rememberUser = false, string remoteError = null)
        {
            if (remoteError != null)
            {
                return BadRequest($"{_errorLocalizer[ErrorMessages.AuthProviderError]} {remoteError}");
            }
            var info = await _signInManager.GetExternalLoginInfoAsync();
            if (info == null)
            {
                return BadRequest(_errorLocalizer[ErrorMessages.AuthProviderError]);
            }

            var email = info.Principal.FindFirstValue(ClaimTypes.Email);
            var user = await _userManager.FindByEmailAsync(email);
            if (user.AdminLocked)
            {
                return BadRequest(_errorLocalizer[ErrorMessages.LockedAccount, _adminOptions.AdminEmailAddress]);
            }

            var model = new LoginViewModel
            {
                ReturnUrl = returnUrl,
                RememberUser = rememberUser
            };
            // Sign in the user with this external login provider if the user already has a login.
            var result = await _signInManager.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, rememberUser);
            if (result.Succeeded)
            {
                _logger.LogInformation(LogEvent.LOGIN_EXTERNAL, "User {USER} logged in with {PROVIDER}.", info.Principal.FindFirstValue(ClaimTypes.Email), info.LoginProvider);
                model.Redirect = true;
                model.Token = GetLoginToken(user, _userManager, _tokenOptions);

                Response.Cookies.Append(
                    CookieRequestCultureProvider.DefaultCookieName,
                    CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(user.Culture)),
                    new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1) });

                return Json(model);
            }
            else
            {
                // If the user does not have an account, then create one. Use the first part of the
                // email (before '@') as the username, since not every provider will have a unique,
                // human-readable, non-personally-identifying username available. The user can always
                // change it.
                user = new ApplicationUser { UserName = email.Substring(0, email.IndexOf('@')), Email = email };
                var newResult = await _userManager.CreateAsync(user);
                if (newResult.Succeeded)
                {
                    newResult = await _userManager.AddLoginAsync(user, info);
                    if (newResult.Succeeded)
                    {
                        await _signInManager.SignInAsync(user, rememberUser);
                        _logger.LogInformation(LogEvent.NEW_ACCOUNT_EXTERNAL, "New account created for {USER} with {PROVIDER}.", user.Email, info.LoginProvider);
                        model.Redirect = true;
                        return Json(model);
                    }
                }
                model.Errors.AddRange(newResult.Errors.Select(e => e.Description));
                return Json(model);
            }
        }

        /// <summary>
        /// Called to initiate a password reset for a user.
        /// </summary>
        /// <param name="model">A <see cref="LoginViewModel"/> used to transfer task data.</param>
        /// <response code="200">Success.</response>
        /// <response code="403">Locked account.</response>
        [HttpPost]
        [ProducesResponseType(typeof(IDictionary<string, string>), 403)]
        [ProducesResponseType(200)]
        public async Task<IActionResult> ForgotPassword([FromBody]LoginViewModel model)
        {
            var user = await _userManager.FindByNameAsync(model.Username);
            if (user == null)
            {
                user = await _userManager.FindByEmailAsync(model.Username);
            }
            if (user == null || !(await _userManager.IsEmailConfirmedAsync(user)))
            {
                // Don't reveal that the user does not exist or is not confirmed
                return Ok();
            }
            if (user.AdminLocked)
            {
                return StatusCode(403, _errorLocalizer[ErrorMessages.LockedAccount, _adminOptions.AdminEmailAddress]);
            }

            // For more information on how to enable account confirmation and password reset please visit https://go.microsoft.com/fwlink/?LinkID=532713
            // Send an email with this link
            var code = await _userManager.GeneratePasswordResetTokenAsync(user);
            var callbackUrl = Url.Action(nameof(ResetPassword), "Account", new { code = code }, protocol: HttpContext.Request.Scheme);
            await _emailSender.SendEmailAsync(model.Username, _emailLocalizer[EmailMessages.PasswordResetEmailSubject],
                $"{_emailLocalizer[EmailMessages.PasswordResetEmailBody]} <a href='{callbackUrl}'>{callbackUrl}</a>");
            _logger.LogInformation(LogEvent.RESET_PW_REQUEST, "Password reset request received for {USER}.", user.Email);
            return Ok();
        }

        /// <summary>
        /// Called to retrieve a list of accepted external authentication providers.
        /// </summary>
        /// <response code="200">A list of accepted external authentication providers.</response>
        [HttpGet]
        [ProducesResponseType(typeof(IDictionary<string, string>), 200)]
        public JsonResult GetAuthProviders()
        {
            return Json(new { providers = _signInManager.GetExternalAuthenticationSchemes().Select(s => s.DisplayName).ToArray() });
        }

        internal static string GetLoginToken(
            ApplicationUser user,
            UserManager<ApplicationUser> userManager,
            TokenProviderOptions tokenOptions)
        {
            var now = DateTime.UtcNow;
            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Email),
                new Claim(ClaimTypes.NameIdentifier, user.Email),
                new Claim(ClaimTypes.Name, user.UserName),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim(JwtRegisteredClaimNames.Iat, (now.Subtract(new DateTime(1970, 1, 1))).TotalSeconds.ToString(), ClaimValueTypes.Integer64)
            };
            var jwt = new JwtSecurityToken(
                claims: claims,
                notBefore: now,
                expires: now.Add(tokenOptions.Expiration),
                signingCredentials: tokenOptions.SigningCredentials);
            var encodedJwt = new JwtSecurityTokenHandler().WriteToken(jwt);
            return encodedJwt;
        }

        /// <summary>
        /// Called to retrieve a list of external authentication providers presently associated with
        /// the current user's account.
        /// </summary>
        /// <response code="400">Invalid user.</response>
        /// <response code="200">A list of external authentication providers.</response>
        [Authorize]
        [HttpGet]
        [ProducesResponseType(typeof(IDictionary<string, string>), 400)]
        [ProducesResponseType(typeof(IDictionary<string, string>), 200)]
        public async Task<IActionResult> GetUserAuthProviders()
        {
            var user = await _userManager.FindByEmailAsync(HttpContext.User.FindFirst(ClaimTypes.NameIdentifier).Value);
            if (user == null)
            {
                return BadRequest(new { error = _errorLocalizer[ErrorMessages.InvalidUserError] });
            }
            var userLogins = await _userManager.GetLoginsAsync(user);
            return Json(new
            {
                providers = _signInManager.GetExternalAuthenticationSchemes().Select(s => s.DisplayName).ToArray(),
                userProviders = userLogins.Select(l => l.ProviderDisplayName).ToArray()
            });
        }

        /// <summary>
        /// Called to determine if the current user account has a local password (may be false if the
        /// user registered with an external authentication provider).
        /// </summary>
        /// <response code="400">Invalid user.</response>
        /// <response code="200">A 'yes' or 'no' response.</response>
        [Authorize]
        [HttpGet]
        [ProducesResponseType(typeof(IDictionary<string, string>), 400)]
        [ProducesResponseType(typeof(IDictionary<string, string>), 200)]
        public async Task<IActionResult> HasPassword()
        {
            var user = await _userManager.FindByEmailAsync(HttpContext.User.FindFirst(ClaimTypes.NameIdentifier).Value);
            if (user == null)
            {
                return BadRequest(_errorLocalizer[ErrorMessages.InvalidUserError]);
            }
            if (await _userManager.HasPasswordAsync(user)) return Json(new { response = "yes" });
            else return Json(new { response = "no" });
        }

        /// <summary>
        /// Called to log in with the provided credentials.
        /// </summary>
        /// <param name="model">A <see cref="LoginViewModel"/> used to transfer task data.</param>
        /// <response code="400">Invalid login attempt.</response>
        /// <response code="403">Locked account.</response>
        /// <response code="200">Login response data.</response>
        [HttpPost]
        [ProducesResponseType(typeof(IDictionary<string, string>), 400)]
        [ProducesResponseType(typeof(IDictionary<string, string>), 403)]
        [ProducesResponseType(typeof(LoginViewModel), 200)]
        public async Task<IActionResult> Login([FromBody]LoginViewModel model)
        {
            // Users can sign in with a username or email address.
            var user = await _userManager.FindByNameAsync(model.Username);
            if (user == null)
            {
                user = await _userManager.FindByEmailAsync(model.Username);
            }
            if (user == null)
            {
                return BadRequest(_errorLocalizer[ErrorMessages.InvalidLogin]);
            }
            else
            {
                if (user.AdminLocked)
                {
                    return StatusCode(403, _errorLocalizer[ErrorMessages.LockedAccount, _adminOptions.AdminEmailAddress]);
                }
                // Require the user to have a confirmed email before they can log in.
                if (!await _userManager.IsEmailConfirmedAsync(user))
                {
                    return BadRequest(_errorLocalizer[ErrorMessages.ConfirmEmailLoginError]);
                }
            }

            // This doesn't count login failures towards account lockout
            // To enable password failures to trigger account lockout, set lockoutOnFailure: true
            var result = await _signInManager.PasswordSignInAsync(user.UserName, model.Password, model.RememberUser, lockoutOnFailure: false);
            if (!result.Succeeded)
            {
                return BadRequest(_errorLocalizer[ErrorMessages.InvalidLogin]);
            }

            model.Token = GetLoginToken(user, _userManager, _tokenOptions);

            Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(user.Culture)),
                new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1) });

            _logger.LogInformation(LogEvent.LOGIN, "User {USER} logged in.", user.Email);
            model.Redirect = true;
            return Json(model);
        }

        /// <summary>
        /// Called to register a new user account with the provided credentials. User accounts cannot
        /// be used immediately after registration; a confirmation email is sent and the link
        /// included must be followed to confirm the address.
        /// </summary>
        /// <param name="model">A <see cref="RegisterViewModel"/> used to transfer task data.</param>
        /// <response code="400">Invalid login attempt.</response>
        /// <response code="403">Locked account.</response>
        /// <response code="200">Success.</response>
        [HttpPost]
        [ProducesResponseType(typeof(IDictionary<string, string>), 400)]
        [ProducesResponseType(typeof(IDictionary<string, string>), 403)]
        [ProducesResponseType(200)]
        public async Task<IActionResult> Register([FromBody]RegisterViewModel model)
        {
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user != null)
            {
                if (user.AdminLocked)
                {
                    return StatusCode(403, _errorLocalizer[ErrorMessages.LockedAccount, _adminOptions.AdminEmailAddress]);
                }
                else if (!user.EmailConfirmed)
                {
                    await SendConfirmationEmail(model, user);
                    return BadRequest(_errorLocalizer[ErrorMessages.ConfirmEmailRegisterError]);
                }
                else
                {
                    return BadRequest(_errorLocalizer[ErrorMessages.DuplicateEmailError]);
                }
            }
            var existingUser = await _userManager.FindByNameAsync(model.Username);
            if (existingUser != null)
            {
                return BadRequest(_errorLocalizer[ErrorMessages.DuplicateUsernameError]);
            }
            var lowerName = model.Username.ToLower();
            if (lowerName.StartsWith("admin") || lowerName.EndsWith("admin") || lowerName.Contains("administrator"))
            {
                return BadRequest(_errorLocalizer[ErrorMessages.OnlyAdminCanBeAdminError]);
            }
            if (lowerName == "system")
            {
                return BadRequest(_errorLocalizer[ErrorMessages.CannotBeSystemError]);
            }
            if (lowerName == "true" || lowerName == "false")
            {
                return BadRequest(_errorLocalizer[ErrorMessages.InvalidNameError]);
            }
            user = new ApplicationUser { UserName = model.Username, Email = model.Email };
            var result = await _userManager.CreateAsync(user, model.Password);
            if (result.Succeeded)
            {
                await SendConfirmationEmail(model, user);

                _logger.LogInformation(LogEvent.NEW_ACCOUNT, "New account created for {USER} with username {USERNAME}.", user.Email, user.UserName);

                return Ok();
            }
            else
            {
                return BadRequest(string.Join(";", result.Errors.Select(e => e.Description)));
            }
        }

        /// <summary>
        /// The endpoint reached when a user clicks a link in an email generated by the ForgotPassword action.
        /// </summary>
        /// <returns>
        /// Redirect to an error page in the event of a bad request; or to a password reset page.
        /// </returns>
        [HttpGet]
        public IActionResult ResetPassword(string code = null)
        {
            return RedirectToAction(nameof(HomeController.Index), new { forwardUrl = code == null ? "/error/400" : $"/user/reset/{code}" });
        }

        /// <summary>
        /// Called to initiate a password reset for a user after following an email link verifying the user.
        /// </summary>
        /// <param name="model">A <see cref="ResetPasswordViewModel"/> used to transfer task data.</param>
        /// <response code="403">Locked account.</response>
        /// <response code="400">Invalid reset attempt.</response>
        /// <response code="200">Success.</response>
        [HttpPost]
        [ProducesResponseType(typeof(IDictionary<string, string>), 403)]
        [ProducesResponseType(typeof(IDictionary<string, string>), 400)]
        [ProducesResponseType(200)]
        public async Task<IActionResult> ResetPassword([FromBody]ResetPasswordViewModel model)
        {
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user != null)
            {
                if (user.AdminLocked)
                {
                    return StatusCode(403, _errorLocalizer[ErrorMessages.LockedAccount, _adminOptions.AdminEmailAddress]);
                }
                var result = await _userManager.ResetPasswordAsync(user, model.Code, model.NewPassword);
                if (result.Succeeded)
                {
                    _logger.LogInformation(LogEvent.RESET_PW_CONFIRM, "Password reset for {USER}.", user.Email);
                    return Ok();
                }
                else
                {
                    return BadRequest(string.Join(";", result.Errors.Select(e => e.Description)));
                }
            }
            // Don't reveal that the user doesn't exist; their login attempt will simply fail.
            return Ok();
        }

        /// <summary>
        /// The endpoint reached when following the link in an email which allows a user to restore
        /// their email address after a change. Can be used to reverse a mistake or unauthorized change.
        /// </summary>
        /// <param name="userId">The primary key of the user to restore. Generated automatically.</param>
        /// <param name="code">A verification code. Generated automatically.</param>
        /// <returns>A JSON object containing an error in the event of a problem; or an OK result.</returns>
        [HttpGet]
        public async Task<IActionResult> RestoreEmail(string userId, string code)
        {
            if (userId == null || code == null)
            {
                return RedirectToAction(nameof(HomeController.Index), new { forwardUrl = "/error/400" });
            }
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null || (user.NewEmail == null && user.OldEmail == null))
            {
                return RedirectToAction(nameof(HomeController.Index), new { forwardUrl = "/error/400" });
            }
            if (user.AdminLocked)
            {
                return RedirectToAction(nameof(HomeController.Index), new { forwardUrl = "/error/400" });
            }
            var result = await _userManager.ConfirmEmailAsync(user, code);
            if (result.Succeeded)
            {
                if (user.NewEmail == null)
                {
                    _logger.LogInformation(LogEvent.EMAIL_CHANGE_REVERT, "Email change reverted from {EMAIL} to {OLDEMAIL}.", user.Email, user.OldEmail);
                    user.Email = user.OldEmail;
                    user.OldEmail = null;
                }
                else
                {
                    _logger.LogInformation(LogEvent.EMAIL_CHANGE_CANCEL, "Email change canceled from {EMAIL} to {NEWEMAIL}.", user.Email, user.NewEmail);
                    user.NewEmail = null;
                }
                await _userManager.UpdateAsync(user);
            }
            return RedirectToAction(nameof(HomeController.Index), new { forwardUrl = result.Succeeded ? "/user/email/restore" : "/error/400" });
        }

        private async Task SendConfirmationEmail(RegisterViewModel model, ApplicationUser user)
        {
            // For more information on how to enable account confirmation and password reset please visit https://go.microsoft.com/fwlink/?LinkID=532713
            // Send an email with this link
            var code = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            var callbackUrl = Url.Action(nameof(ConfirmEmail), "Account", new { userId = user.Id, code = code }, protocol: HttpContext.Request.Scheme);
            await _emailSender.SendEmailAsync(model.Email, _emailLocalizer[EmailMessages.ConfirmAccountEmailSubject],
                $"{_emailLocalizer[EmailMessages.ConfirmAccountEmailBody]} <a href='{callbackUrl}'>link</a>");
        }

        /// <summary>
        /// Called to get information about the user with the given username. Admin only.
        /// </summary>
        /// <param name="username">The username to verify.</param>
        /// <response code="400">Bad request.</response>
        /// <response code="404">No such user.</response>
        /// <response code="200">User data.</response>
        [Authorize]
        [HttpPost("{username}")]
        [ProducesResponseType(typeof(IDictionary<string, string>), 400)]
        [ProducesResponseType(404)]
        [ProducesResponseType(typeof(UserViewModel), 200)]
        public async Task<IActionResult> VerifyUser(string username)
        {
            var email = HttpContext.User.FindFirst(ClaimTypes.NameIdentifier).Value;
            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
            {
                return BadRequest(_errorLocalizer[ErrorMessages.InvalidUserError]);
            }
            if (user.AdminLocked)
            {
                return BadRequest(_errorLocalizer[ErrorMessages.LockedAccount, _adminOptions.AdminEmailAddress]);
            }
            // Only Admins may get information about a user.
            var roles = await _userManager.GetRolesAsync(user);
            if (!roles.Contains(CustomRoles.Admin))
            {
                return BadRequest(_errorLocalizer[ErrorMessages.AdminOnlyError]);
            }

            var targetUser = await _userManager.FindByNameAsync(username);
            if (targetUser == null)
            {
                return NotFound();
            }
            return Json(new UserViewModel
            {
                Email = targetUser.Email,
                IsLocked = targetUser.AdminLocked,
                Username = targetUser.UserName
            });
        }
    }
}
