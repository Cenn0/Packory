// Isolated entry point. Never start Packory's HTTP/MCP application.
await Packory.DatabaseConnectorWorkerHost.RunAsync();
