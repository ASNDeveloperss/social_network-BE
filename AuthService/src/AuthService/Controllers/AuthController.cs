namespace AuthService.Controllers;  // Должно быть ТОЧНО так

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;
using AuthService.DTOs.Requests;
using AuthService.DTOs.Responses;
using AuthService.Interfaces;
using AuthService.Models;
using AuthService.DTO.Responses;

[ApiController]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IAuthService authService,
        UserManager<ApplicationUser> userManager,
        ILogger<AuthController> logger)
    {
        _authService = authService;
        _userManager = userManager;
        _logger = logger;
    }

    [HttpPost("register")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        try
        {
            _logger.LogInformation("Registration attempt for email: {Email}", request.Email);

            var result = await _authService.RegisterAsync(request);

            _logger.LogInformation("User registered successfully: {Email}", request.Email);

            return CreatedAtAction(nameof(Register), result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Registration failed for email: {Email}", request.Email);

            if (ex.Message.Contains("already taken") || ex.Message.Contains("already exists"))
            {
                return Conflict(new ErrorResponse
                {
                    Message = "User with this email already exists"
                });
            }

            return BadRequest(new ErrorResponse
            {
                Message = "Registration failed",
                Errors = new List<string> { ex.Message }
            });
        }
    }

    [HttpPost("login")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        try
        {
            _logger.LogInformation("Login attempt for email: {Email}", request.Email);

            var result = await _authService.LoginAsync(request);

            _logger.LogInformation("User logged in successfully: {Email}", request.Email);

            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning("Failed login attempt for email: {Email}", request.Email);

            return Unauthorized(new ErrorResponse
            {
                Message = "Invalid email or password"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login error for email: {Email}", request.Email);

            return StatusCode(500, new ErrorResponse
            {
                Message = "An error occurred during login"
            });
        }
    }

    [HttpGet("test")]
    public IActionResult Test()
    {
        return Ok(new { message = "Auth service is working!" });
    }

    // ========== ДОБАВЛЯЕМ НОВЫЕ ЗАЩИЩЕННЫЕ ЭНДПОИНТЫ ==========

    /// <summary>
    /// AC-06: Получение профиля текущего пользователя
    /// Требуется валидный JWT токен
    /// </summary>
    [Authorize]
    [HttpGet("me")]
    [ProducesResponseType(typeof(ProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMyProfile()
    {
        try
        {
            // Получаем ID пользователя из JWT токена
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning("GetMyProfile: User ID not found in token claims");
                return Unauthorized(new ErrorResponse
                {
                    Message = "Invalid token",
                    Errors = new List<string> { "Token does not contain user information" }
                });
            }

            // Ищем пользователя
            var user = await _userManager.FindByIdAsync(userId);

            if (user == null)
            {
                return NotFound(new ErrorResponse
                {
                    Message = "User not found"
                });
            }

            // Получаем роли
            var roles = await _userManager.GetRolesAsync(user);

            // Создаем ответ
            var response = new ProfileResponse
            {
                Id = user.Id,
                Email = user.Email!,
                UserName = user.UserName!,
                FirstName = user.FirstName,
                LastName = user.LastName,
                PhoneNumber = user.PhoneNumber,
                EmailConfirmed = user.EmailConfirmed,
                CreatedAt = user.CreatedAt,
                UpdatedAt = user.UpdatedAt,
                LastLoginAt = user.LastLoginAt,
                IsActive = user.IsActive,
                FailedLoginAttempts = user.FailedLoginAttempts,
                LockoutEnd = user.LockoutEnd,
                Roles = roles.ToList()
            };

            _logger.LogInformation("Profile retrieved for user: {Email}", user.Email);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetMyProfile error");
            return StatusCode(500, new ErrorResponse
            {
                Message = "Internal server error"
            });
        }
    }

    /// <summary>
    /// AC-07: Тестовый защищенный эндпоинт (демонстрация работы авторизации)
    /// Без валидного токена вернет 401 Unauthorized
    /// </summary>
    [Authorize]
    [HttpGet("protected")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public IActionResult GetProtectedData()
    {
        var userInfo = new
        {
            UserId = User.FindFirstValue(ClaimTypes.NameIdentifier),
            Email = User.FindFirstValue(ClaimTypes.Email),
            IsAuthenticated = User.Identity?.IsAuthenticated ?? false
        };

        return Ok(new
        {
            Message = "This is protected data",
            AccessTime = DateTime.UtcNow,
            User = userInfo
        });
    }

    /// <summary>
    /// Проверка валидности JWT токена
    /// </summary>
    [Authorize]
    [HttpGet("validate-token")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public IActionResult ValidateToken()
    {
        return Ok(new
        {
            Valid = true,
            Message = "Token is valid",
            UserId = User.FindFirstValue(ClaimTypes.NameIdentifier),
            Email = User.FindFirstValue(ClaimTypes.Email)
        });
    }

    /// <summary>
    /// Обновление профиля пользователя (защищенный эндпоинт)
    /// </summary>
    [Authorize]
    [HttpPut("profile")]
    [ProducesResponseType(typeof(ProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request)
    {
        try
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var user = await _userManager.FindByIdAsync(userId);

            if (user == null)
            {
                return Unauthorized(new ErrorResponse
                {
                    Message = "User not found"
                });
            }

            // Обновляем только не-null поля
            if (!string.IsNullOrWhiteSpace(request.FirstName))
                user.FirstName = request.FirstName;

            if (!string.IsNullOrWhiteSpace(request.LastName))
                user.LastName = request.LastName;

            if (!string.IsNullOrWhiteSpace(request.PhoneNumber))
                user.PhoneNumber = request.PhoneNumber;

            user.UpdatedAt = DateTime.UtcNow;

            var result = await _userManager.UpdateAsync(user);

            if (!result.Succeeded)
            {
                var errors = result.Errors.Select(e => e.Description).ToList();
                return BadRequest(new ErrorResponse
                {
                    Message = "Failed to update profile",
                    Errors = errors
                });
            }

            // Возвращаем обновленный профиль
            var roles = await _userManager.GetRolesAsync(user);
            var response = new ProfileResponse
            {
                Id = user.Id,
                Email = user.Email!,
                UserName = user.UserName!,
                FirstName = user.FirstName,
                LastName = user.LastName,
                PhoneNumber = user.PhoneNumber,
                EmailConfirmed = user.EmailConfirmed,
                CreatedAt = user.CreatedAt,
                UpdatedAt = user.UpdatedAt,
                LastLoginAt = user.LastLoginAt,
                IsActive = user.IsActive,
                FailedLoginAttempts = user.FailedLoginAttempts,
                LockoutEnd = user.LockoutEnd,
                Roles = roles.ToList()
            };

            _logger.LogInformation("Profile updated for user: {Email}", user.Email);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateProfile error");
            return StatusCode(500, new ErrorResponse
            {
                Message = "Internal server error"
            });
        }
    }
}