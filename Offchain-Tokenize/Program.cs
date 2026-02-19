using Microsoft.EntityFrameworkCore;
using Offchain_Tokenize.Configuration;
using Offchain_Tokenize.Models;
using Offchain_Tokenize.Services;
using System;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.Configure<CreWorkflowOptions>(
    builder.Configuration.GetSection(CreWorkflowOptions.SectionName));
builder.Services.AddHttpClient<ICreWorkflowClient, CreWorkflowClient>();
var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
