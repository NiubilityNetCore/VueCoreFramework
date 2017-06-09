﻿using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MVCCoreVue.Data;
using MVCCoreVue.Extensions;
using MVCCoreVue.Models;
using MVCCoreVue.Models.AccountViewModels;
using MVCCoreVue.Services;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace MVCCoreVue.Controllers
{
    [Route("api/[controller]/[action]")]
    public class AccountController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly IEmailSender _emailSender;
        private readonly ILogger<AccountController> _logger;
        private readonly TokenProviderOptions _options;

        public AccountController(
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            IEmailSender emailSender,
            ILogger<AccountController> logger,
            IOptions<TokenProviderOptions> options)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _emailSender = emailSender;
            _logger = logger;
            _options = options.Value;
        }

        [Authorize]
        [HttpGet]
        public async Task<IActionResult> Authorize(string dataType = null, string operation = "View", string id = null)
        {
            var email = HttpContext.User.FindFirst(ClaimTypes.NameIdentifier).Value;
            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
            {
                return Json(new AuthorizationViewModel { Authorization = "unauthorized" });
            }

            var token = await GetLoginToken(email, user);

            // If no specific data is being requested, just being a recognized user is sufficient authorization.
            if (string.IsNullOrEmpty(dataType))
            {
                return Json(new AuthorizationViewModel { Email = user.Email, Token = token, Authorization = "authorized" });
            }

            // First authorization for all data is checked.
            if (user.Claims.Any(c => c.ClaimType == "All" && c.ClaimValue == "All"))
            {
                return Json(new AuthorizationViewModel { Email = user.Email, Token = token, Authorization = "authorized" });
            }

            // If not authorized for all data, authorization for the specific data type is checked.
            var claimValue = dataType.ToInitialCaps();
            // First, authorization for all operations on the data is checked.
            if (user.Claims.Any(c => c.ClaimType == "All" && c.ClaimValue == claimValue))
            {
                return Json(new AuthorizationViewModel { Email = user.Email, Token = token, Authorization = "authorized" });
            }

            // If not authorized for all operations, the specific operation is checked.
            // In the absence of a specific operation, the default action is View.
            var claimType = operation.ToInitialCaps();
            if (user.Claims.Any(c => c.ClaimType == claimType && c.ClaimValue == claimValue))
            {
                return Json(new AuthorizationViewModel { Email = user.Email, Token = token, Authorization = "authorized" });
            }

            // If not authorized for the operation on the data type and an id is provided,
            // the specific item is checked.
            if (!string.IsNullOrEmpty(id))
            {
                // First, authorization for all operations on the item is checked.
                if (user.Claims.Any(c => c.ClaimType == "All" && c.ClaimValue == id))
                {
                    return Json(new AuthorizationViewModel { Email = user.Email, Token = token, Authorization = "authorized" });
                }

                // If not authorized for all operations, the specific operation is checked.
                if (user.Claims.Any(c => c.ClaimType == claimType && c.ClaimValue == id))
                {
                    return Json(new AuthorizationViewModel { Email = user.Email, Token = token, Authorization = "authorized" });
                }
            }

            // No authorizations found.
            return Json(new AuthorizationViewModel { Email = user.Email, Token = token, Authorization = "unauthorized" });
        }

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
            var result = await _userManager.ConfirmEmailAsync(user, code);
            if (result.Succeeded)
            {
                _logger.LogInformation(LogEvent.EMAIL_CHANGE_CONFIRM, "Email change confirmed from {OLDEMAIL} to {NEWEMAIL}.", user.Email, user.NewEmail);
                user.OldEmail = user.Email;
                user.Email = user.NewEmail;
                user.NewEmail = null;
            }
            return RedirectToAction(nameof(HomeController.Index), new { forwardUrl = result.Succeeded ? "/user/email/confirm" : "/error/400" });
        }

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

        [HttpPost]
        public IActionResult ExternalLogin([FromBody]LoginViewModel model)
        {
            var provider = _signInManager.GetExternalAuthenticationSchemes().SingleOrDefault(a => a.DisplayName == model.AuthProvider);
            if (provider == null)
            {
                model.Errors.Add("There was a problem authorizing with that provider.");
                _logger.LogWarning(LogEvent.EXTERNAL_PROVIDER_NOTFOUND, "Could not find provider {PROVIDER}.", model.AuthProvider);
                return new JsonResult(model);
            }
            // Request a redirect to the external login provider.
            var redirectUrl = Url.Action(nameof(ExternalLoginCallback), "Account", new { ReturnUrl = model.ReturnUrl, RememberUser = model.RememberMe });
            var properties = _signInManager.ConfigureExternalAuthenticationProperties(provider.AuthenticationScheme, redirectUrl);
            return Challenge(properties, provider.AuthenticationScheme);
        }

        [HttpGet]
        public async Task<LoginViewModel> ExternalLoginCallback(string returnUrl = null, bool rememberUser = false, string remoteError = null)
        {
            var model = new LoginViewModel
            {
                ReturnUrl = returnUrl,
                RememberMe = rememberUser
            };
            if (remoteError != null)
            {
                model.Errors.Add("There was a problem authorizing with that provider: {remoteError}");
                return model;
            }
            var info = await _signInManager.GetExternalLoginInfoAsync();
            if (info == null)
            {
                model.Errors.Add("There was a problem authorizing with that provider.");
                return model;
            }

            var email = info.Principal.FindFirstValue(ClaimTypes.Email);

            // Sign in the user with this external login provider if the user already has a login.
            var result = await _signInManager.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, rememberUser);
            if (result.Succeeded)
            {
                _logger.LogInformation(LogEvent.LOGIN_EXTERNAL, "User {USER} logged in with {PROVIDER}.", info.Principal.FindFirstValue(ClaimTypes.Email), info.LoginProvider);
                model.Redirect = true;
                var user = await _userManager.FindByEmailAsync(email);
                model.Token = await GetLoginToken(model.Email, user);
                return model;
            }
            else
            {
                // If the user does not have an account, then create one.
                var user = new ApplicationUser { UserName = email, Email = email };
                var newResult = await _userManager.CreateAsync(user);
                if (newResult.Succeeded)
                {
                    newResult = await _userManager.AddLoginAsync(user, info);
                    if (newResult.Succeeded)
                    {
                        await _signInManager.SignInAsync(user, rememberUser);
                        _logger.LogInformation(LogEvent.NEW_ACCOUNT_EXTERNAL, "New account created for {USER} with {PROVIDER}.", user.Email, info.LoginProvider);
                        model.Redirect = true;
                        return model;
                    }
                }
                model.Errors.AddRange(newResult.Errors.Select(e => e.Description));
                return model;
            }
        }

        [HttpPost]
        public async Task ForgotPassword([FromBody]LoginViewModel model)
        {
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null || !(await _userManager.IsEmailConfirmedAsync(user)))
            {
                // Don't reveal that the user does not exist or is not confirmed
                return;
            }

            // For more information on how to enable account confirmation and password reset please visit https://go.microsoft.com/fwlink/?LinkID=532713
            // Send an email with this link
            var code = await _userManager.GeneratePasswordResetTokenAsync(user);
            var callbackUrl = Url.Action(nameof(ResetPassword), "Account", new { userId = user.Id, code = code }, protocol: HttpContext.Request.Scheme);
            await _emailSender.SendEmailAsync(model.Email, "Reset Password",
                $"Please reset your password by clicking here: <a href='{callbackUrl}'>{callbackUrl}</a>");
            _logger.LogInformation(LogEvent.RESET_PW_REQUEST, "Password reset request received for {USER}.", user.Email);
            return;
        }

        [HttpGet]
        public JsonResult GetAuthProviders()
        {
            return Json(new { providers = _signInManager.GetExternalAuthenticationSchemes().Select(s => s.DisplayName).ToArray() });
        }

        private async Task<string> GetLoginToken(string email, ApplicationUser user)
        {
            var now = DateTime.UtcNow;
            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, email),
                new Claim(ClaimTypes.NameIdentifier, email),
                new Claim(ClaimTypes.Name, email),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim(JwtRegisteredClaimNames.Iat, (now.Subtract(new DateTime(1970, 1, 1))).TotalSeconds.ToString(), ClaimValueTypes.Integer64)
            };
            var roles = await _userManager.GetRolesAsync(user);
            if (roles != null)
            {
                claims.AddRange(roles.Select(r => new Claim("role", r)));
            }
            var jwt = new JwtSecurityToken(
                claims: claims,
                notBefore: now,
                expires: now.Add(_options.Expiration),
                signingCredentials: _options.SigningCredentials);
            var encodedJwt = new JwtSecurityTokenHandler().WriteToken(jwt);
            return encodedJwt;
        }

        [Authorize]
        [HttpGet]
        public async Task<IActionResult> GetUserAuthProviders()
        {
            var user = await _userManager.FindByEmailAsync(HttpContext.User.FindFirst(ClaimTypes.NameIdentifier).Value);
            if (user == null)
            {
                return RedirectToAction(nameof(HomeController.Index), new { forwardUrl = "/error/400" });
            }
            var userLogins = await _userManager.GetLoginsAsync(user);
            return Json(new
            {
                providers = _signInManager.GetExternalAuthenticationSchemes().Select(s => s.DisplayName).ToArray(),
                userProviders = userLogins.Select(l => l.ProviderDisplayName).ToArray()
            });
        }

        [Authorize]
        [HttpGet]
        public async Task<IActionResult> HasPassword()
        {
            var user = await _userManager.FindByEmailAsync(HttpContext.User.FindFirst(ClaimTypes.NameIdentifier).Value);
            if (user == null)
            {
                return RedirectToAction(nameof(HomeController.Index), new { forwardUrl = "/error/400" });
            }
            if (await _userManager.HasPasswordAsync(user)) return Json(new { response = "yes" });
            else return Json(new { response = "no" });
        }

        [HttpPost]
        public async Task<LoginViewModel> Login([FromBody]LoginViewModel model)
        {
            // Require the user to have a confirmed email before they can log in.
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null)
            {
                model.Errors.Add("Invalid login attempt.");
                return model;
            }
            else
            {
                if (!await _userManager.IsEmailConfirmedAsync(user))
                {
                    model.Errors.Add("You must have a confirmed email to log in. Please check your email for your confirmation link. If you've lost the email, please register again.");
                    return model;
                }
            }

            // This doesn't count login failures towards account lockout
            // To enable password failures to trigger account lockout, set lockoutOnFailure: true
            var result = await _signInManager.PasswordSignInAsync(model.Email, model.Password, model.RememberMe, lockoutOnFailure: false);
            if (!result.Succeeded)
            {
                model.Errors.Add("Invalid login attempt.");
                return model;
            }

            model.Token = await GetLoginToken(model.Email, user);

            _logger.LogInformation(LogEvent.LOGIN, "User {USER} logged in.", user.Email);
            model.Redirect = true;
            return model;
        }

        [HttpPost]
        public async Task Logout()
        {
            var user = await _userManager.FindByEmailAsync(HttpContext.User.FindFirst(ClaimTypes.NameIdentifier).Value);
            if (user == null) _logger.LogInformation(LogEvent.LOGOUT, "Unknown user logged out.");
            else _logger.LogInformation(LogEvent.LOGOUT, "User {USER} logged out.", user.Email);
            await _signInManager.SignOutAsync();
        }

        [HttpPost]
        public async Task<RegisterViewModel> Register([FromBody]RegisterViewModel model)
        {
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user != null)
            {
                if (!user.EmailConfirmed)
                {
                    await SendConfirmationEmail(model, user);
                    model.Errors.Add("An account with this email has already been registered, but your email address has not been confirmed. A new link has just been sent, in case the last one got lost. Please check your spam if you don't see it after a few minutes.");
                }
                else
                {
                    model.Errors.Add("An account with this email already exists. If you've forgotten your password, please use the link on the login page.");
                }
                return model;
            }
            user = new ApplicationUser { UserName = model.Email, Email = model.Email };
            var result = await _userManager.CreateAsync(user, model.Password);
            if (result.Succeeded)
            {
                await SendConfirmationEmail(model, user);

                _logger.LogInformation(LogEvent.NEW_ACCOUNT, "New account created for {USER}.", user.Email);

                model.Redirect = true;
            }
            else
            {
                model.Errors.AddRange(result.Errors.Select(e => e.Description));
            }

            return model;
        }

        [HttpGet]
        public IActionResult ResetPassword(string code = null)
        {
            return RedirectToAction(nameof(HomeController.Index), new { forwardUrl = code == null ? "/error/400" : $"/user/reset/{code}" });
        }

        [HttpPost]
        public async Task<ResetPasswordViewModel> ResetPassword([FromBody]ResetPasswordViewModel model)
        {
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user != null)
            {
                var result = await _userManager.ResetPasswordAsync(user, model.Code, model.NewPassword);
                if (result.Succeeded)
                {
                    _logger.LogInformation(LogEvent.RESET_PW_CONFIRM, "Password reset for {USER}.", user.Email);
                    return model;
                }
                model.Errors.AddRange(result.Errors.Select(e => e.Description));
            }
            // Don't reveal that the user doesn't exist; their login attempt will simply fail.
            model.Code = null;
            return model;
        }

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
            }
            return RedirectToAction(nameof(HomeController.Index), new { forwardUrl = result.Succeeded ? "/user/email/restore" : "/error/400" });
        }

        private async Task SendConfirmationEmail(RegisterViewModel model, ApplicationUser user)
        {
            // For more information on how to enable account confirmation and password reset please visit https://go.microsoft.com/fwlink/?LinkID=532713
            // Send an email with this link
            var code = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            var callbackUrl = Url.Action(nameof(ConfirmEmail), "Account", new { userId = user.Id, code = code }, protocol: HttpContext.Request.Scheme);
            await _emailSender.SendEmailAsync(model.Email, "Confirm your account",
                $"Please confirm your account by clicking this link: <a href='{callbackUrl}'>link</a>");
        }
    }
}
