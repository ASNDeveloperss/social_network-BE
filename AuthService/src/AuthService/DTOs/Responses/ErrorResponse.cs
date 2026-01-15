namespace AuthService.DTOs.Responses;

public class ErrorResponse
{
    public string Message { get; set; } = string.Empty;
    public List<string> Errors { get; set; } = new();
    public string? TraceId { get; set; }
}