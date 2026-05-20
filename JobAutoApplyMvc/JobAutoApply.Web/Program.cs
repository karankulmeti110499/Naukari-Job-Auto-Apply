using JobAutoApply.Web.Options;
using JobAutoApply.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Allow Render (and similar platforms) to inject the listening port.
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.Configure<GeminiOptions>(builder.Configuration.GetSection(GeminiOptions.SectionName));
builder.Services.Configure<NaukriOptions>(builder.Configuration.GetSection(NaukriOptions.SectionName));

builder.Services.AddScoped<IResumeTextExtractorService, ResumeTextExtractorService>();
builder.Services.AddScoped<INaukriAutomationService, NaukriAutomationService>();
builder.Services.AddScoped<IExcelJobDatabaseService, ExcelJobDatabaseService>();
builder.Services.AddHttpClient<IGeminiResumeAnalyzerService, GeminiResumeAnalyzerService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Jobs}/{action=Index}/{id?}");

app.Run();
