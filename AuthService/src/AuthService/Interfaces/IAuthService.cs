namespace AuthService.Interfaces;

using AuthService.DTOs.Requests;
using AuthService.DTOs.Responses;

public interface IAuthService
{
    Task<AuthResponse> RegisterAsync(RegisterRequest request);
    Task<AuthResponse> LoginAsync(LoginRequest request);
}