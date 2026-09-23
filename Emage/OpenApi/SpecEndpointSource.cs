namespace Emage.OpenApi;

public sealed record RegisteredEndpoint(string Method, string Route, string OperationId);

public static class SpecEndpointSource
{
	private static readonly string[] HttpMethods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

	public static IReadOnlyList<RegisteredEndpoint> Read(string specPath)
	{
		var endpoints = new List<RegisteredEndpoint>();
		string? path = null;
		string? method = null;
		bool inPaths = false;

		foreach (string rawLine in File.ReadLines(specPath))
		{
			if (string.IsNullOrWhiteSpace(rawLine)) continue;
			var line = rawLine.Trim();
			if (line.StartsWith('#')) continue;

			int indent = rawLine.Length - rawLine.TrimStart().Length;
			if (line == "paths:" || line.StartsWith("paths: #"))
			{
				inPaths = true;
				continue;
			}
			if (!inPaths) continue;
			if (indent == 0)
			{
				inPaths = false;
				continue;
			}
			if (indent == 2 && line.StartsWith('/'))
			{
				path = line.TrimEnd(':');
				method = null;
				continue;
			}
			if (indent == 4)
			{
				string candidate = line.Split(':', 2)[0].Trim();
				if (Array.IndexOf(HttpMethods, candidate) >= 0)
				{
					method = candidate.ToUpperInvariant();
					continue;
				}
			}
			if (indent == 6 && line.StartsWith("operationId:"))
			{
				if (path is null || method is null) continue;
				string operationId = line.Split(':', 2)[1].Trim();
				endpoints.Add(new RegisteredEndpoint(method, ToAspNetCoreRoute(path), operationId));
			}
		}

		return endpoints;
	}

	public static string ToAspNetCoreRoute(string template) =>
		template.Replace("{path}", "{**path}");
}