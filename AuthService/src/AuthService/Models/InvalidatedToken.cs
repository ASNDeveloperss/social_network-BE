namespace AuthService.Models;

public class InvalidatedToken
{
    public int Id { get; set; }
    public string Jti { get; set; } = string.Empty; // JWT ID
    public DateTime ExpiresAt { get; set; }
    public DateTime InvalidatedAt { get; set; }
    public string? Reason { get; set; }
}