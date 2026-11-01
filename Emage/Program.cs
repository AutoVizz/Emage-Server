using Emage.Imaging.J2k;
using Emage.OpenApi;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;

var builder = WebApplication.CreateBuilder(args);

string? uploadPassword = builder.Configuration["Emage:UploadPassword"];
string specPath = builder.Configuration["Emage:OpenApiSpec"]
	?? Path.Combine(AppContext.BaseDirectory, "openapi.yaml");

builder.Services.AddSingleton(sp =>
{
	string configured = builder.Configuration["Emage:ImageRoot"]
		?? Path.Combine(builder.Environment.ContentRootPath, "images");
	return new J2kImageLibrary(configured);
});

builder.Services.AddOpenApi();
// Every X-Emage-* response header must be listed here or browser JS cannot read
// it cross-origin. The server sets five: Framing, Layers, TotalLayers,
// RemainingLayers and TilePsotOffsets.
var emageResponseHeaders = new[]
{
	"X-Emage-Framing", "X-Emage-Layers", "X-Emage-TotalLayers",
	"X-Emage-RemainingLayers", "X-Emage-TilePsotOffsets"
};
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
	policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()
		.WithExposedHeaders(emageResponseHeaders)));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
	app.UseHsts();
}

app.UseHttpsRedirection();
app.UseCors();
app.UseStaticFiles();
// Registered after UseCors so the CORS middleware still has a say in the
// response: an unhandled exception used to return a bare 500 with no
// Access-Control-Allow-Origin, which a browser reports as "CORS failed" and
// hides the real status. Handling it here keeps server faults diagnosable.
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
	Exception? error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
	app.Logger.LogError(error, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
	context.Response.StatusCode = StatusCodes.Status500InternalServerError;
	await context.Response.WriteAsJsonAsync(new ProblemDetails
	{
		Status = StatusCodes.Status500InternalServerError,
		Title = "Internal server error",
		Detail = error?.Message,
		Instance = context.Request.Path
	});
}));

var library = app.Services.GetRequiredService<J2kImageLibrary>();
var handlers = new EndpointHandlers(library, uploadPassword);
var specEndpoints = SpecEndpointSource.Read(specPath);

foreach (var endpoint in specEndpoints)
{
	RouteHandlerBuilder route = endpoint switch
	{
		{ Method: "GET", OperationId: "getImage" } => app.MapGet(endpoint.Route, handlers.GetImage),
		{ Method: "POST", OperationId: "uploadImage" } => app.MapPost(endpoint.Route, handlers.UploadImage),
		{ Method: "DELETE", OperationId: "deleteImage" } => app.MapDelete(endpoint.Route, handlers.DeleteImage),
		{ Method: "GET", OperationId: "getImageInfo" } => app.MapGet(endpoint.Route, handlers.GetImageInfo),
		{ Method: "GET", OperationId: "listImages" } => app.MapGet(endpoint.Route, handlers.ListImages),
		_ => throw new InvalidOperationException($"Unsupported spec operation '{endpoint.Method} {endpoint.OperationId}'")
	};
	route.AddOpenApiOperationTransformer((operation, _, _) =>
	{
		operation.OperationId = endpoint.OperationId;
		return Task.CompletedTask;
	});
}

app.MapGet("/openapi/compare", (HttpContext http) =>
{
	var liveEndpoints = http.RequestServices.GetRequiredService<EndpointDataSource>().Endpoints
		.OfType<RouteEndpoint>()
		.Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>() is not null)
		.Select(e => (
			Method: e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.First(),
			Route: NormalizeTemplate(e.RoutePattern.RawText ?? "")))
		.Where(l => l.Route.StartsWith("image"))
		.ToHashSet();

	var contractSet = specEndpoints
		.Where(e => e.Route.StartsWith("/image"))
		.Select(e => (e.Method, Route: NormalizeTemplate(e.Route)))
		.ToHashSet();

	var onlyInSpec = contractSet.Except(liveEndpoints).Select(p => "/" + p.Route).ToList();
	var onlyServed = liveEndpoints.Except(contractSet).Select(p => "/" + p.Route).ToList();

	return Results.Json(new
	{
		inSync = onlyInSpec.Count == 0 && onlyServed.Count == 0,
		specEndpoints = specEndpoints.Select(e => new { e.Method, e.Route, e.OperationId }),
		generatedEndpoints = liveEndpoints.OrderBy(p => p.Route).Select(p => p.Method + " " + "/" + p.Route),
		onlyInSpec,
		onlyServed
	});
});

app.MapOpenApi();

app.Run();

static string NormalizeTemplate(string template) =>
	template.TrimStart('/').Replace("*", "");