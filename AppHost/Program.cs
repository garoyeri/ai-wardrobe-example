var builder = DistributedApplication.CreateBuilder(args);

var ollama = builder.AddOllama("ollama")
    .WithImageTag("0.20.8-rc0")
    .WithDataVolume("ollama-data")
    .WithOpenWebUI(c =>
    {
        c.WithImageTag("0.8.12");
    });

var model = ollama.AddModel("model", "granite4:3b");

var api = builder.AddProject<Projects.Api>("api")
    .WithReference(ollama)
    .WithReference(model)
    .WithEnvironment("Ollama__Model", model.Resource.ModelName);

builder.AddProject<Projects.Web>("web")
    .WithReference(api)
    .WithExternalHttpEndpoints();

builder.Build().Run();
