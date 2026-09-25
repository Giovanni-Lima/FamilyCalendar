using FamilyCalendar.Data;
using FamilyCalendar.Domain;
using FamilyCalendar.Services;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// In produzione (Render) la porta arriva dall'env PORT; in locale vale launchSettings.json.
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// Connessione MySQL: env FAMILYCALENDAR_CONNECTION, altrimenti ConnectionStrings:FamilyCalendar.
// ServerVersion fissa: evita la connessione all'avvio solo per rilevarla (Aiven è MySQL 8).
var connectionString = Environment.GetEnvironmentVariable("FAMILYCALENDAR_CONNECTION")
                       ?? builder.Configuration.GetConnectionString("FamilyCalendar");

builder.Services.AddDbContext<FamilyCalendarDbContext>(o =>
    o.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 36)),
        my => my.EnableRetryOnFailure()));

builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<PushSender>();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Migration all'avvio + seed dei membri iniziali (env FAMILYCALENDAR_MEMBERS, es.
// "Giovanni Lima:admin;Maria Rossi") solo se la tabella Members è vuota.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FamilyCalendarDbContext>();
    db.Database.Migrate();

    var seed = Environment.GetEnvironmentVariable("FAMILYCALENDAR_MEMBERS") ?? app.Configuration["Seed:Members"];
    if (!string.IsNullOrWhiteSpace(seed) && !db.Members.Any())
    {
        foreach (var entry in seed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':', 2, StringSplitOptions.TrimEntries);
            db.Members.Add(new Member
            {
                DisplayName = parts[0],
                Role = parts.Length > 1 && parts[1].Equals("admin", StringComparison.OrdinalIgnoreCase)
                    ? MemberRole.Amministratore : MemberRole.Membro,
            });
        }
        db.SaveChanges();
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });

app.MapControllers();
app.Run();
