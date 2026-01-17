using System.Text;
using AuthService.Data;
using AuthService.Interfaces;
using AuthService.Models;
using AuthService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Конфигурация
var configuration = builder.Configuration;

// БАЗА ДАННЫХ - ОБНОВЛЕННЫЙ КОД ДЛЯ RENDER
var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL");
if (string.IsNullOrEmpty(connectionString))
{
    // Для локальной разработки
    connectionString = configuration.GetConnectionString("DefaultConnection");
}
else if (connectionString.StartsWith("postgresql://"))
{
    // Конвертируем Render URL в стандартный формат
    var uri = new Uri(connectionString);
    var userInfo = uri.UserInfo.Split(':');

    connectionString = $"Host={uri.Host};" +
                      $"Port={uri.Port};" +
                      $"Database={uri.LocalPath.TrimStart('/')};" +
                      $"Username={userInfo[0]};" +
                      $"Password={userInfo[1]};" +
                      $"SSL Mode=Require;Trust Server Certificate=true";
}

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString, npgsqlOptions =>
    {
        npgsqlOptions.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(30),
            errorCodesToAdd: null);
    }));

// Identity
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequiredLength = 8;
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = false;
    options.User.RequireUniqueEmail = true;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    options.Lockout.MaxFailedAccessAttempts = 5;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

// JWT Authentication - ОБНОВЛЕНО ДЛЯ RENDER
var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET")
    ?? configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("JWT secret is not configured");

var key = Encoding.UTF8.GetBytes(jwtSecret);

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(key),
        ValidateIssuer = true,
        ValidIssuer = Environment.GetEnvironmentVariable("JWT_ISSUER")
                    ?? configuration["Jwt:Issuer"]
                    ?? "auth-service",
        ValidateAudience = true,
        ValidAudience = Environment.GetEnvironmentVariable("JWT_AUDIENCE")
                       ?? configuration["Jwt:Audience"]
                       ?? "microservices-client",
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero
    };

    // Для разработки - отключаем HTTPS требование
    if (builder.Environment.IsDevelopment())
    {
        options.RequireHttpsMetadata = false;
    }
});

// Регистрация сервисов
builder.Services.AddScoped<IAuthService, AuthService.Services.AuthService>();

// Контроллеры
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Swagger - только для разработки
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "Auth Service",
            Version = "v1"
        });

        options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Enter JWT Bearer token"
        });

        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "Bearer"
                    }
                },
                Array.Empty<string>()
            }
        });
    });
}

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var allowedOrigins = Environment.GetEnvironmentVariable("ALLOWED_ORIGINS")
                           ?? configuration["AllowedOrigins"]
                           ?? "*";

        if (allowedOrigins == "*")
        {
            policy.SetIsOriginAllowed(_ => true);
        }
        else
        {
            policy.WithOrigins(allowedOrigins.Split(';'));
        }

        policy.AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

// Порядок Middleware ВАЖЕН!
app.UseCors();

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "Healthy", timestamp = DateTime.UtcNow }));

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.MapGet("/debug/routes", () =>
    {
        var routes = app.Services.GetService<IEnumerable<EndpointDataSource>>()
            ?.SelectMany(es => es.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => new
            {
                Method = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods?.FirstOrDefault(),
                Pattern = e.RoutePattern.RawText,
                DisplayName = e.DisplayName
            })
            .ToList();

        return Results.Json(routes);
    }).WithTags("Debug");
}
else
{
    // В продакшене всегда HTTPS
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Применение миграций
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await dbContext.Database.MigrateAsync();
}

// Получаем порт из переменной окружения (Render использует PORT)
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://*:{port}");