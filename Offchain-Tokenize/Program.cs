using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Offchain_Tokenize.Configuration;
using Offchain_Tokenize.Models;
using Offchain_Tokenize.Services;
using System;
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Offchain Tokenize API",
        Version = "v1",
        Description = "API for offchain tokenization workflows and related entities."
    });

    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }
});
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.Configure<CreWorkflowOptions>(
    builder.Configuration.GetSection(CreWorkflowOptions.SectionName));
builder.Services.AddHttpClient<ICreWorkflowClient, CreWorkflowClient>();
var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Offchain Tokenize API v1");
    options.RoutePrefix = "swagger";
});

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
