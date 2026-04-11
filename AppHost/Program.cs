var builder = DistributedApplication.CreateBuilder(args);

var ollama = builder.AddOllama("ollama")
    .WithImageTag("0.20.2")
    .WithDataVolume("ollama-data")
    .WithOpenWebUI(c =>
    {
       c.WithImageTag("0.8.12"); 
    });

var model = ollama.AddModel("model", "llama3.2:1b");

var api = builder.AddProject<Projects.Api>("api")
    .WithReference(ollama)
    .WithReference(model)
    .WithEnvironment("Ollama__Model", "llama3.2:1b");

builder.AddProject<Projects.Web>("web")
    .WithReference(api)
    .WithExternalHttpEndpoints();

builder.Build().Run();
