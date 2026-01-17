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

// ЛОГИРОВАНИЕ СТАРТА
Console.WriteLine("=== APPLICATION STARTING ===");
Console.WriteLine($"Environment: {builder.Environment.EnvironmentName}");

// Конфигурация
var configuration = builder.Configuration;

// БАЗА ДАННЫХ - ОБНОВЛЕННЫЙ КОД ДЛЯ RENDER
var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL");
Console.WriteLine($"DATABASE_URL exists: {!string.IsNullOrEmpty(connectionString)}");

if (string.IsNullOrEmpty(connectionString))
{
    // Для локальной разработки
    connectionString = configuration.GetConnectionString("DefaultConnection");
    Console.WriteLine("⚠️ Using appsettings.json connection string");
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
    Console.WriteLine($"✅ Converted Render URL for: {uri.Host}");
}

// Проверяем что connectionString не пустой
if (string.IsNullOrEmpty(connectionString))
{
    Console.WriteLine("❌ CRITICAL: No database connection string found!");
}
else
{
    Console.WriteLine($"✅ Connection string length: {connectionString.Length}");
}

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString, npgsqlOptions =>
    {
        npgsqlOptions.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(30),
            errorCodesToAdd: null);
        npgsqlOptions.CommandTimeout(60); // 60 секунд timeout
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

// JWT Authentication - ИСПРАВЛЕННАЯ ВЕРСИЯ
var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET")
    ?? configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("JWT secret is not configured");

var key = Encoding.UTF8.GetBytes(jwtSecret);
Console.WriteLine($"✅ JWT Secret configured: {jwtSecret.Length > 0}");

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
        ClockSkew = TimeSpan.FromSeconds(30), // 30 секунд допуска для разных серверов
        RequireExpirationTime = true,

        // КРИТИЧЕСКИ ВАЖНЫЕ НАСТРОЙКИ ДЛЯ FIX IDX10503:
        RequireSignedTokens = true,
        TryAllIssuerSigningKeys = true, // Пробуем все ключи без проверки kid

        // Отключаем кэширование провайдеров подписи
        CryptoProviderFactory = new CryptoProviderFactory()
        {
            CacheSignatureProviders = false
        }
    };

    // Для разработки - отключаем HTTPS требование
    if (builder.Environment.IsDevelopment())
    {
        options.RequireHttpsMetadata = false;
    }

    options.IncludeErrorDetails = true; // Для дебага показываем детали ошибок
    options.SaveToken = true; // Сохраняем токен для доступа в контроллерах
});

// Регистрация сервисов
builder.Services.AddScoped<IAuthService, AuthService.Services.AuthService>();

// Контроллеры
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Swagger - ВКЛЮЧАЕМ ДЛЯ ВСЕХ СРЕД
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Auth Service API",
        Version = "v1",
        Description = "Authentication Service for Microservices"
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

// CORS - РАЗРЕШАЕМ ВСЕ БЕЗ ОГРАНИЧЕНИЙ
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.SetIsOriginAllowed(_ => true)  // ← РАЗРЕШАЕТ ЛЮБОЙ ORIGIN!
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()
              .SetPreflightMaxAge(TimeSpan.FromHours(1));
    });
});

var app = builder.Build();

Console.WriteLine("🔄 Building middleware pipeline...");

// CORS - ВКЛЮЧАЕМ РАЗРЕШЕНИЕ ВСЕХ ORIGINS
app.UseCors("AllowAll");

// Swagger ВСЕГДА ВКЛЮЧАЕМ
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Auth Service API v1");
    options.RoutePrefix = "api-docs"; // Доступно на /api-docs
    options.DocumentTitle = "Auth Service API";
});


// Проверка переменных окружения
app.MapGet("/api/debug/env", () =>
{
    var envVars = new Dictionary<string, string>
    {
        ["ASPNETCORE_ENVIRONMENT"] = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Not set",
        ["DATABASE_URL_SET"] = (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DATABASE_URL"))).ToString(),
        ["JWT_SECRET_SET"] = (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JWT_SECRET"))).ToString(),
        ["JWT_SECRET_LENGTH"] = (Environment.GetEnvironmentVariable("JWT_SECRET")?.Length ?? 0).ToString(),
        ["JWT_ISSUER"] = Environment.GetEnvironmentVariable("JWT_ISSUER") ?? "Not set",
        ["JWT_AUDIENCE"] = Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? "Not set",
        ["PORT"] = Environment.GetEnvironmentVariable("PORT") ?? "8080"
    };
    return Results.Json(envVars);
});

// Проверка подключения к БД
app.MapGet("/api/debug/db", async (ApplicationDbContext db) =>
{
    try
    {
        var canConnect = await db.Database.CanConnectAsync();
        return Results.Ok(new
        {
            connected = canConnect,
            database = db.Database.GetDbConnection().Database,
            provider = db.Database.ProviderName,
            connectionState = db.Database.GetDbConnection().State.ToString(),
            connectionStringExists = !string.IsNullOrEmpty(db.Database.GetConnectionString())
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Database error: {ex.Message}");
    }
});

// Health endpoint
app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy",
    service = "Auth Service",
    timestamp = DateTime.UtcNow,
    environment = app.Environment.EnvironmentName,
    version = "1.0.0"
}));

// HTTPS редирект для продакшена
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Применение миграций С ОБРАБОТКОЙ ОШИБОК
try
{
    Console.WriteLine("🔄 Applying database migrations...");
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await dbContext.Database.MigrateAsync();
    Console.WriteLine("✅ Database migrations applied successfully");
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Database migration failed: {ex.Message}");
    if (ex.InnerException != null)
    {
        Console.WriteLine($"❌ Inner exception: {ex.InnerException.Message}");
    }
    // НЕ ПАДАЕМ, продолжаем работу
}

// Запуск
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
Console.WriteLine($"🚀 Starting Auth Service on port: {port}");
Console.WriteLine($"🔗 Health check: http://localhost:{port}/health");
Console.WriteLine($"📚 API Docs: http://localhost:{port}/api-docs");
Console.WriteLine($"🌐 CORS: ALLOWED ALL ORIGINS (Any IP/Domain)");

app.Run($"http://*:{port}");