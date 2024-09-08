﻿using Dwt.Api.Services;
using Dwt.Shared.Identity;
using Dwt.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json.Serialization;

namespace Dwt.Api.Controllers.Samples;

public class UsersController(IOptions<IdentityOptions> identityOptions, UserManager<DwtUser> userManager) : ApiBaseController
{
	public struct UserResponse
	{
		public string Id { get; set; }
		public string Username { get; set; }
		public string Email { get; set; }
		public IList<string> Roles { get; set; }
	}

	/// <summary>
	/// Returns current user's information.
	/// </summary>
	/// <returns></returns>
	[HttpGet("/api/users/-me")]
	[Authorize]
	public async Task<ActionResult<ApiResp<UserResponse>>> GetMyInfo()
	{
		var userId = GetUserID(identityOptions.Value);
		var user = userManager.Users.FirstOrDefault(u => u.Id == userId);
		if (user == null)
		{
			return _respAuthenticationRequired;
		}
		var userResponse = new UserResponse
		{
			Id = user.Id,
			Username = user.UserName!,
			Email = user.Email!,
			Roles = await userManager.GetRolesAsync(user)
		};
		return ResponseOk(userResponse);
	}

	/// <summary>
	/// Fetches all users.
	/// </summary>
	/// <returns></returns>
	/// <remarks>Only ADMIN role can see all users.</remarks>
	[HttpGet("/api/users")]
	[Authorize(Roles = DwtRole.ROLE_NAME_ADMIN)]
	public async Task<ActionResult<ApiResp<UserResponse>>> GetAll()
	{
		var users = await userManager.Users.ToListAsync();
		var userResponses = users.ConvertAll(u => new UserResponse
		{
			Id = u.Id,
			Username = u.UserName!,
			Email = u.Email!,
			Roles = userManager.GetRolesAsync(u).Result
		});
		return ResponseOk(userResponses);
	}

	/// <summary>
	/// Fetches a user by ID.
	/// </summary>
	/// <param name="id"></param>
	/// <returns></returns>
	/// <remarks>
	/// - Users can only see their own information.
	/// - ADMIN role can see all users.
	/// </remarks>
	[HttpGet("/api/users/{id}")]
	[Authorize]
	public async Task<ActionResult<ApiResp<User>>> GetByID(string id)
	{
		var user = await userManager.Users.FirstOrDefaultAsync(u => u.Id == id);
		if (user == null)
		{
			return _respNotFound;
		}

		var userId = GetUserID(identityOptions.Value);
		var currentUser = userManager.Users.FirstOrDefault(u => u.Id == userId);
		if (currentUser == null)
		{
			//should not happen
			return _respAuthenticationRequired;
		}
		var currentUserRoles = await userManager.GetRolesAsync(currentUser);

		if (!currentUserRoles.Contains(DwtRole.ROLE_NAME_ADMIN) && currentUser.Id == user.Id)
		{
			// return "Not Found" in this case to avoid leaking the fact that the user exists
			// return _respAccessDenied;
			return _respNotFound;
		}

		return ResponseOk(new UserResponse
		{
			Id = user.Id,
			Username = user.UserName!,
			Email = user.Email!,
			Roles = userManager.GetRolesAsync(user).Result
		});
	}

	/*----------------------------------------------------------------------*/

	public struct ChangePwdReq
	{
		[JsonPropertyName("old_password")]
		public string OldPassword { get; set; }

		[JsonPropertyName("new_password")]
		public string NewPassword { get; set; }
	}

	public struct ChangePwdResp
	{
		public string Message { get; set; }

		public string Token { get; set; }
	}

	[HttpPost("/api/users/-me/password")]
	[Authorize]
	public async Task<ActionResult<ApiResp<ChangePwdResp>>> ChangeMyPassword(
		[FromBody] ChangePwdReq req,
		IPasswordValidator<DwtUser> passwordValidator,
		IAuthenticator? authenticator, IAuthenticatorAsync? authenticatorAsync)
	{
		if (authenticator == null && authenticatorAsync == null)
		{
			throw new ArgumentNullException("No authenticator defined.", (Exception?)null);
		}

		var jwtToken = GetAuthToken();
		var tokenValidationResult = await ValidateAuthTokenAsync(authenticator, authenticatorAsync, jwtToken!);
		if (tokenValidationResult.Status != 200)
		{
			return ResponseNoData(403, tokenValidationResult.Error);
		}

		var userId = GetUserID(identityOptions.Value);
		var user = await userManager.Users.FirstOrDefaultAsync(u => u.Id == userId);
		if (user == null || !await userManager.CheckPasswordAsync(user, req.OldPassword))
		{
			return ResponseNoData(403, "Invalid user/password combination.");
		}

		var iresult = await passwordValidator.ValidateAsync(userManager, user, req.NewPassword);
		if (iresult != IdentityResult.Success)
		{
			return ResponseNoData(400, iresult.ToString());
		}

		iresult = await userManager.ChangePasswordAsync(user, req.OldPassword, req.NewPassword);
		if (iresult != IdentityResult.Success)
		{
			return ResponseNoData(500, iresult.ToString());
		}

		var refreshResult = authenticatorAsync != null
			? await authenticatorAsync.RefreshAsync(jwtToken!, true)
			: authenticator?.Refresh(jwtToken!, true);

		return ResponseOk(new ChangePwdResp
		{
			Message = "Password changed successfully.",
			Token = refreshResult!.Token!, // changing password should invalidate all previous auth tokens
		});
	}

	/*----------------------------------------------------------------------*/

	public struct CreateUserReq
	{
		[JsonPropertyName("username")]
		public string Username { get; set; }

		[JsonPropertyName("password")]
		public string Password { get; set; }

		[JsonPropertyName("email")]
		public string Email { get; set; }
	}

	public struct CreateUserResp
	{
		public string Message { get; set; }

		public string Id { get; set; }
	}

	[HttpPost("/api/users")]
	[Authorize(Policy = DwtIdentity.POLICY_NAME_ADMIN_OR_CREATE_ACCOUNT_PERM)]
	public async Task<ActionResult<ApiResp<CreateUserResp>>> CreateUser(
		[FromBody] CreateUserReq req,
		UserManager<DwtUser> userManager,
		IPasswordValidator<DwtUser> passwordValidator,
		IAuthenticator? authenticator, IAuthenticatorAsync? authenticatorAsync)
	{
		if (authenticator == null && authenticatorAsync == null)
		{
			throw new ArgumentNullException("No authenticator defined.", (Exception?)null);
		}

		var jwtToken = GetAuthToken();
		var tokenValidationResult = await ValidateAuthTokenAsync(authenticator, authenticatorAsync, jwtToken!);
		if (tokenValidationResult.Status != 200)
		{
			return ResponseNoData(403, tokenValidationResult.Error);
		}

		if (await userManager.FindByNameAsync(req.Username) != null)
		{
			return ResponseNoData(400, $"User with username '{req.Username}' already exists.");
		}
		if (await userManager.FindByEmailAsync(req.Email) != null)
		{
			return ResponseNoData(400, $"User with email '{req.Email}' already exists.");
		}

		if (await passwordValidator.ValidateAsync(userManager, null, req.Password) != IdentityResult.Success)
		{
			return ResponseNoData(400, "Password does not meet complexity requirements.");
		}

		return ResponseOk();
	}
}
