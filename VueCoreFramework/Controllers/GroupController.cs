﻿using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using VueCoreFramework.Data;
using VueCoreFramework.Models;
using VueCoreFramework.Services;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace VueCoreFramework.Controllers
{
    /// <summary>
    /// An MVC controller for handling group membership tasks.
    /// </summary>
    [Authorize]
    [Route("api/[controller]/[action]")]
    public class GroupController : Controller
    {
        private readonly AdminOptions _adminOptions;
        private readonly ApplicationDbContext _context;
        private readonly IEmailSender _emailSender;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly UserManager<ApplicationUser> _userManager;

        /// <summary>
        /// Initializes a new instance of <see cref="GroupController"/>.
        /// </summary>
        public GroupController(
            IOptions<AdminOptions> adminOptions,
            ApplicationDbContext context,
            IEmailSender emailSender,
            RoleManager<IdentityRole> roleManager,
            UserManager<ApplicationUser> userManager)
        {
            _adminOptions = adminOptions.Value;
            _context = context;
            _emailSender = emailSender;
            _roleManager = roleManager;
            _userManager = userManager;
        }

        /// <summary>
        /// The endpoint reached when a user clicks a link in an email generated by the InviteUserToGroup action.
        /// </summary>
        /// <param name="userId">
        /// The ID of the user to add to the group.
        /// </param>
        /// <param name="groupId">The ID of the group to which the user will be added.</param>
        /// <returns>
        /// Redirect to an error page in the event of a bad request; or to the group management page if successful.
        /// </returns>
        [HttpGet]
        public async Task<IActionResult> AddUserToGroup(string userId, string groupId, string code)
        {
            if (userId == null || code == null)
            {
                return RedirectToAction(nameof(HomeController.Index), new { forwardUrl = "/error/400" });
            }
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null || user.AdminLocked)
            {
                return RedirectToAction(nameof(HomeController.Index), new { forwardUrl = "/error/400" });
            }
            var groupRole = await _roleManager.FindByIdAsync(groupId);
            if (groupRole == null)
            {
                return RedirectToAction(nameof(HomeController.Index), new { forwardUrl = "/error/400" });
            }
            var result = await _userManager.ConfirmEmailAsync(user, code);
            if (!result.Succeeded)
            {
                return RedirectToAction(nameof(HomeController.Index), new { forwardUrl = "/error/400" });
            }

            await _userManager.AddToRoleAsync(user, groupRole.Name);
            return RedirectToAction(nameof(HomeController.Index), new { forwardUrl = "/group/manage" });
        }

        /// <summary>
        /// Called to get information about the given group.
        /// </summary>
        /// <param name="group">The name of the group to retrieve.</param>
        /// <returns>
        /// An error if there is a problem; a message indicating no results if there is no match; or
        /// a <see cref="GroupViewModel"/>.
        /// </returns>
        [HttpPost("{group}")]
        public async Task<IActionResult> GetGroup(string group)
        {
            var email = HttpContext.User.FindFirst(ClaimTypes.NameIdentifier).Value;
            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
            {
                return Json(new { error = ErrorMessages.InvalidUserError });
            }
            if (user.AdminLocked)
            {
                return Json(new { error = ErrorMessages.LockedAccount(_adminOptions.AdminEmailAddress) });
            }
            var roles = await _userManager.GetRolesAsync(user);
            if (group == CustomRoles.SiteAdmin)
            {
                if (roles.Contains(CustomRoles.SiteAdmin))
                {
                    return Json(new { error = ErrorMessages.SiteAdminSingularError });
                }
                else
                {
                    return Json(new { error = ErrorMessages.SiteAdminOnlyError });
                }
            }
            else if (group == CustomRoles.Admin)
            {
                if (!roles.Contains(CustomRoles.SiteAdmin))
                {
                    return Json(new { error = ErrorMessages.SiteAdminOnlyError });
                }
            }
            else if (group == CustomRoles.AllUsers)
            {
                return Json(new { error = ErrorMessages.AllUsersRequiredError });
            }
            // Only Admins can retrieve information about a group by name.
            else if (!roles.Contains(CustomRoles.Admin))
            {
                return Json(new { error = ErrorMessages.AdminOnlyError });
            }

            var groupRole = await _roleManager.FindByNameAsync(group);
            if (groupRole == null)
            {
                return Json(new { response = ResponseMessages.NoResults });
            }
            var managerId = _context.UserClaims.FirstOrDefault(c =>
                c.ClaimType == CustomClaimTypes.PermissionGroupManager && c.ClaimValue == groupRole.Name)?
                .UserId;
            var manager = await _userManager.FindByIdAsync(managerId);
            var members = await _userManager.GetUsersInRoleAsync(groupRole.Name);
            return Json(new GroupViewModel
            {
                Name = groupRole.Name,
                Manager = manager?.UserName,
                Members = members.Select(m => m.UserName).ToList()
            });
        }

        /// <summary>
        /// Called to find all groups to which the current user belongs.
        /// </summary>
        /// <returns>An error if there is a problem; or a list of <see cref="GroupViewModel"/>s.</returns>
        [HttpGet]
        public async Task<IActionResult> GetGroupMemberships()
        {
            var email = HttpContext.User.FindFirst(ClaimTypes.NameIdentifier).Value;
            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
            {
                return Json(new { error = ErrorMessages.InvalidUserError });
            }
            if (user.AdminLocked)
            {
                return Json(new { error = ErrorMessages.LockedAccount(_adminOptions.AdminEmailAddress) });
            }

            var groups = await _userManager.GetRolesAsync(user);
            // Do not include the site admin or all users special roles.
            groups = groups.Where(g => g != CustomRoles.SiteAdmin && g != CustomRoles.AllUsers).ToList();

            List<GroupViewModel> vms = new List<GroupViewModel>();
            foreach (var group in groups)
            {
                string managerName = null;
                if (group == CustomRoles.Admin)
                {
                    var siteAdmin = await _userManager.GetUsersInRoleAsync(CustomRoles.SiteAdmin);
                    managerName = siteAdmin.FirstOrDefault().UserName;
                }
                else
                {
                    var managerId = _context.UserClaims.FirstOrDefault(c =>
                        c.ClaimType == CustomClaimTypes.PermissionGroupManager && c.ClaimValue == group)?
                        .UserId;
                    var manager = await _userManager.FindByIdAsync(managerId);
                    managerName = manager?.UserName;
                }
                var members = await _userManager.GetUsersInRoleAsync(group);
                vms.Add(new GroupViewModel
                {
                    Name = group,
                    Manager = managerName,
                    Members = members.Select(m => m.UserName).ToList()
                });
            }
            return Json(vms);
        }

        /// <summary>
        /// Called to invite a user to a group.
        /// </summary>
        /// <param name="username">
        /// The username of the user to invite to the group.
        /// </param>
        /// <param name="group">The name of the group to which the user will be invited.</param>
        /// <returns>An error if there is a problem; or a response indicating success.</returns>
        [HttpPost("{username}/{group}")]
        public async Task<IActionResult> InviteUserToGroup(string username, string group)
        {
            var email = HttpContext.User.FindFirst(ClaimTypes.NameIdentifier).Value;
            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
            {
                return Json(new { error = ErrorMessages.InvalidUserError });
            }
            if (user.AdminLocked)
            {
                return Json(new { error = ErrorMessages.LockedAccount(_adminOptions.AdminEmailAddress) });
            }
            if (username == user.UserName)
            {
                return Json(new { error = ErrorMessages.SelfGroupAddError });
            }
            var roles = await _userManager.GetRolesAsync(user);
            if (group == CustomRoles.SiteAdmin)
            {
                if (roles.Contains(CustomRoles.SiteAdmin))
                {
                    return Json(new { error = ErrorMessages.SiteAdminSingularError });
                }
                else
                {
                    return Json(new { error = ErrorMessages.SiteAdminOnlyError });
                }
            }
            else if (group == CustomRoles.Admin)
            {
                if (!roles.Contains(CustomRoles.SiteAdmin))
                {
                    return Json(new { error = ErrorMessages.SiteAdminOnlyError });
                }
            }
            else if (group == CustomRoles.AllUsers)
            {
                return Json(new { error = ErrorMessages.AllUsersRequiredError });
            }
            // Admins can invite users to any non-admin group, regardless of their own membership.
            else if (!roles.Contains(CustomRoles.Admin))
            {
                var claims = await _userManager.GetClaimsAsync(user);
                if (!claims.Any(c => c.Type == CustomClaimTypes.PermissionGroupManager && c.Value == group))
                {
                    return Json(new { error = ErrorMessages.ManagerOnlyError });
                }
            }

            var groupRole = await _roleManager.FindByNameAsync(group);
            if (groupRole == null)
            {
                return Json(new { error = ErrorMessages.InvalidTargetGroupError });
            }
            var targetUser = await _userManager.FindByNameAsync(username);
            if (targetUser == null)
            {
                if (roles.Contains(CustomRoles.Admin))
                {
                    return Json(new { error = ErrorMessages.InvalidTargetUserError });
                }
                // Non-admins are not permitted to know the identities of other users who are not
                // members of common groups. Therefore, indicate success despite there being no such
                // member, to avoid exposing a way to determine valid usernames.
                else
                {
                    return Json(new { response = ResponseMessages.Success });
                }
            }

            // Generate an email with a callback URL pointing to the 'AddUserToGroup' action.
            var confirmCode = await _userManager.GenerateEmailConfirmationTokenAsync(targetUser);
            var acceptCallbackUrl = Url.Action(nameof(GroupController.AddUserToGroup), "Group", new { userId = targetUser.Id, groupId = groupRole.Id, code = confirmCode }, protocol: HttpContext.Request.Scheme);
            await _emailSender.SendEmailAsync(targetUser.Email, "You've been invited to join a group",
                $"You've been invited to join the {group} group. If you would like to accept the invitation, please click this link to become a group member: <a href='{acceptCallbackUrl}'>link</a>");

            return Json(new { response = ResponseMessages.Success });
        }

        /// <summary>
        /// Called to remove the current user from the given group.
        /// </summary>
        /// <param name="group">The name of the group to leave.</param>
        /// <returns>An error if there is a problem; or a response indicating success.</returns>
        [HttpPost("{group}")]
        public async Task<IActionResult> LeaveGroup(string group)
        {
            var email = HttpContext.User.FindFirst(ClaimTypes.NameIdentifier).Value;
            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
            {
                return Json(new { error = ErrorMessages.InvalidUserError });
            }
            if (user.AdminLocked)
            {
                return Json(new { error = ErrorMessages.LockedAccount(_adminOptions.AdminEmailAddress) });
            }
            if (group == CustomRoles.SiteAdmin)
            {
                return Json(new { error = ErrorMessages.SiteAdminSingularError });
            }
            else if (group == CustomRoles.AllUsers)
            {
                return Json(new { error = ErrorMessages.AllUsersRequiredError });
            }
            var groupRole = await _roleManager.FindByNameAsync(group);
            if (groupRole == null)
            {
                return Json(new { error = ErrorMessages.InvalidTargetGroupError });
            }
            var managerId = _context.UserClaims.FirstOrDefault(c =>
                c.ClaimType == CustomClaimTypes.PermissionGroupManager && c.ClaimValue == group)?
                .UserId;
            if (managerId == user.Id)
            {
                return Json(new { error = ErrorMessages.MustHaveManagerError });
            }
            await _userManager.RemoveFromRoleAsync(user, group);
            return Json(new { response = ResponseMessages.Success });
        }

        /// <summary>
        /// Called to remove the given group.
        /// </summary>
        /// <param name="group">The name of the group to remove.</param>
        /// <returns>An error if there is a problem; or a response indicating success.</returns>
        [HttpPost("{group}")]
        public async Task<IActionResult> RemoveGroup(string group)
        {
            var email = HttpContext.User.FindFirst(ClaimTypes.NameIdentifier).Value;
            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
            {
                return Json(new { error = ErrorMessages.InvalidUserError });
            }
            if (user.AdminLocked)
            {
                return Json(new { error = ErrorMessages.LockedAccount(_adminOptions.AdminEmailAddress) });
            }
            var roles = await _userManager.GetRolesAsync(user);
            if (group == CustomRoles.SiteAdmin)
            {
                if (roles.Contains(CustomRoles.SiteAdmin))
                {
                    return Json(new { error = ErrorMessages.SiteAdminSingularError });
                }
                else
                {
                    return Json(new { error = ErrorMessages.SiteAdminOnlyError });
                }
            }
            else if (group == CustomRoles.Admin)
            {
                return Json(new { error = ErrorMessages.AdminRequiredError });
            }
            else if (group == CustomRoles.AllUsers)
            {
                return Json(new { error = ErrorMessages.AllUsersRequiredError });
            }
            // Admins can delete any non-admin group, regardless of their own membership.
            else if (!roles.Contains(CustomRoles.Admin))
            {
                var claims = await _userManager.GetClaimsAsync(user);
                if (!claims.Any(c => c.Type == CustomClaimTypes.PermissionGroupManager && c.Value == group))
                {
                    return Json(new { error = ErrorMessages.ManagerOnlyError });
                }
            }

            var groupRole = await _roleManager.FindByNameAsync(group);
            if (groupRole == null)
            {
                return Json(new { error = ErrorMessages.InvalidTargetGroupError });
            }
            // Delete group messages
            var groupMessages = _context.Messages.Where(m => m.GroupRecipient == groupRole);
            _context.RemoveRange(groupMessages);
            await _context.SaveChangesAsync();
            await _roleManager.DeleteAsync(groupRole);
            return Json(new { response = ResponseMessages.Success });
        }

        /// <summary>
        /// Called to remove the given user from the given group.
        /// </summary>
        /// <param name="username">The name of the user to remove from the group.</param>
        /// <param name="group">The group from which to remove the user.</param>
        /// <returns>An error if there is a problem; or a response indicating success.</returns>
        [HttpPost("{username}/{group}")]
        public async Task<IActionResult> RemoveUserFromGroup(string username, string group)
        {
            var email = HttpContext.User.FindFirst(ClaimTypes.NameIdentifier).Value;
            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
            {
                return Json(new { error = ErrorMessages.InvalidUserError });
            }
            if (user.AdminLocked)
            {
                return Json(new { error = ErrorMessages.LockedAccount(_adminOptions.AdminEmailAddress) });
            }
            var roles = await _userManager.GetRolesAsync(user);
            if (group == CustomRoles.SiteAdmin)
            {
                if (roles.Contains(CustomRoles.SiteAdmin))
                {
                    return Json(new { error = ErrorMessages.SiteAdminSingularError });
                }
                else
                {
                    return Json(new { error = ErrorMessages.SiteAdminOnlyError });
                }
            }
            else if (group == CustomRoles.Admin)
            {
                if (!roles.Contains(CustomRoles.SiteAdmin))
                {
                    return Json(new { error = ErrorMessages.SiteAdminOnlyError });
                }
            }
            else if (group == CustomRoles.AllUsers)
            {
                return Json(new { error = ErrorMessages.AllUsersRequiredError });
            }
            // Admins can remove users from any non-admin group, regardless of their own membership.
            else if (!roles.Contains(CustomRoles.Admin))
            {
                var claims = await _userManager.GetClaimsAsync(user);
                if (!claims.Any(c => c.Type == CustomClaimTypes.PermissionGroupManager && c.Value == group))
                {
                    return Json(new { error = ErrorMessages.ManagerOnlyError });
                }
            }

            var groupRole = await _roleManager.FindByNameAsync(group);
            if (groupRole == null)
            {
                return Json(new { error = ErrorMessages.InvalidTargetGroupError });
            }
            var targetUser = await _userManager.FindByNameAsync(username);
            if (targetUser == null)
            {
                return Json(new { error = ErrorMessages.InvalidTargetUserError });
            }
            await _userManager.RemoveFromRoleAsync(targetUser, groupRole.Name);
            return Json(new { response = ResponseMessages.Success });
        }

        /// <summary>
        /// Called to create a new group with the given name, with the current user as its manager.
        /// </summary>
        /// <param name="group">The name of the group to create.</param>
        /// <returns>An error if there is a problem; or a response indicating success.</returns>
        [HttpPost("{group}")]
        public async Task<IActionResult> StartNewGroup(string group)
        {
            var email = HttpContext.User.FindFirst(ClaimTypes.NameIdentifier).Value;
            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
            {
                return Json(new { error = ErrorMessages.InvalidUserError });
            }
            if (user.AdminLocked)
            {
                return Json(new { error = ErrorMessages.LockedAccount(_adminOptions.AdminEmailAddress) });
            }
            var lowerGroup = group.ToLower();
            if (lowerGroup.StartsWith("admin") || lowerGroup.EndsWith("admin") || lowerGroup.Contains("administrator"))
            {
                return Json(new { error = ErrorMessages.OnlyAdminCanBeAdminError });
            }
            if (lowerGroup == "true" || lowerGroup == "false")
            {
                return Json(new { error = ErrorMessages.InvalidNameError });
            }
            var groupRole = await _roleManager.FindByNameAsync(group);
            if (groupRole != null)
            {
                return Json(new { error = ErrorMessages.DuplicateGroupNameError });
            }
            var role = new IdentityRole(group);
            await _roleManager.CreateAsync(role);
            await _userManager.AddToRoleAsync(user, group);
            await _userManager.AddClaimAsync(user, new Claim(CustomClaimTypes.PermissionGroupManager, group));
            return Json(new { response = ResponseMessages.Success });
        }

        /// <summary>
        /// Called to transfer management of the given group to the given user.
        /// </summary>
        /// <param name="username">The name of the user who is to be the new manager.</param>
        /// <param name="group">The name of the group whose manager is to change.</param>
        /// <returns>An error if there is a problem; or a response indicating success.</returns>
        [HttpPost("{username}/{group}")]
        public async Task<IActionResult> TransferManagerToUser(string username, string group)
        {
            var email = HttpContext.User.FindFirst(ClaimTypes.NameIdentifier).Value;
            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
            {
                return Json(new { error = ErrorMessages.InvalidUserError });
            }
            if (user.AdminLocked)
            {
                return Json(new { error = ErrorMessages.LockedAccount(_adminOptions.AdminEmailAddress) });
            }
            var roles = await _userManager.GetRolesAsync(user);
            if (group == CustomRoles.SiteAdmin || group == CustomRoles.Admin)
            {
                return Json(new { error = ErrorMessages.AdminNoManagerError });
            }
            // Admins can transfer the manager role of any non-admin group, regardless of membership.
            else if (!roles.Contains(CustomRoles.Admin))
            {
                var claims = await _userManager.GetClaimsAsync(user);
                if (!claims.Any(c => c.Type == CustomClaimTypes.PermissionGroupManager && c.Value == group))
                {
                    return Json(new { error = ErrorMessages.ManagerOnlyError });
                }
            }

            var groupRole = await _roleManager.FindByNameAsync(group);
            if (groupRole == null)
            {
                return Json(new { error = ErrorMessages.InvalidTargetGroupError });
            }
            var targetUser = await _userManager.FindByNameAsync(username);
            if (targetUser == null)
            {
                return Json(new { error = ErrorMessages.InvalidTargetUserError });
            }
            var member = await _userManager.IsInRoleAsync(targetUser, group);
            if (!member)
            {
                // An admin may transfer the membership role of a group to anyone, even a user who
                // was not previously a member of that group. Doing so automatically adds the user to
                // the group.
                if (roles.Contains(CustomRoles.Admin))
                {
                    await _userManager.AddToRoleAsync(targetUser, group);
                }
                // The manager may only transfer the membership role to an existing member of the group.
                else
                {
                    return Json(new { error = ErrorMessages.GroupMemberOnlyError });
                }
            }
            _context.UserClaims.Remove(_context.UserClaims.FirstOrDefault(c => c.ClaimType == CustomClaimTypes.PermissionGroupManager && c.ClaimValue == group));
            await _userManager.AddClaimAsync(targetUser, new Claim(CustomClaimTypes.PermissionGroupManager, group));

            _context.Messages.Add(new Message
            {
                Content = $"**{targetUser.UserName}** has been made the manager of **{group}**.",
                IsSystemMessage = true,
                GroupRecipient = groupRole,
                GroupRecipientName = groupRole.Name
            });
            await _context.SaveChangesAsync();

            return Json(new { response = ResponseMessages.Success });
        }

        /// <summary>
        /// Called to transfer the site admin role to the given user.
        /// </summary>
        /// <param name="username">The name of the user who is to be the new site admin.</param>
        /// <returns>An error if there is a problem; or a response indicating success.</returns>
        [HttpPost("{username}")]
        public async Task<IActionResult> TransferSiteAdminToUser(string username)
        {
            var email = HttpContext.User.FindFirst(ClaimTypes.NameIdentifier).Value;
            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
            {
                return Json(new { error = ErrorMessages.InvalidUserError });
            }
            if (user.AdminLocked)
            {
                return Json(new { error = ErrorMessages.LockedAccount(_adminOptions.AdminEmailAddress) });
            }
            var roles = await _userManager.GetRolesAsync(user);
            if (!roles.Contains(CustomRoles.SiteAdmin))
            {
                return Json(new { error = ErrorMessages.SiteAdminOnlyError });
            }

            var targetUser = await _userManager.FindByNameAsync(username);
            if (targetUser == null)
            {
                return Json(new { error = ErrorMessages.InvalidTargetUserError });
            }
            await _userManager.AddToRoleAsync(targetUser, CustomRoles.SiteAdmin);
            await _userManager.RemoveFromRoleAsync(user, CustomRoles.SiteAdmin);

            _context.Messages.Add(new Message
            {
                Content = $"You have been made the new site administrator. Please contact the former site administrator (**{user.UserName}**) with any questions about this role.",
                IsSystemMessage = true,
                SingleRecipient = targetUser,
                SingleRecipientName = targetUser.UserName
            });
            await _context.SaveChangesAsync();

            return Json(new { response = ResponseMessages.Success });
        }
    }
}
