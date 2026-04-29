var builder = DistributedApplication.CreateBuilder(args);

var ollama = builder.AddOllama("ollama")
    .WithImageTag("0.21.2")
    .WithDataVolume("ollama-data")
    .WithOpenWebUI(c =>
    {
        c.WithImageTag("0.9.2");
    });

var model = ollama.AddModel("model", "granite4:3b");
var embeddings = ollama.AddModel("embeddings", "mxbai-embed-large:335m");

var vectorDb = builder.AddQdrant("vector-db")
    .WithImageTag("v1.17.1")
    .WithDataVolume("qdrant-data");

var api = builder.AddProject<Projects.Api>("api")
    .WithReference(ollama)
    .WithReference(model)
    .WithReference(embeddings)
    .WithReference(vectorDb)
    .WithEnvironment("Ollama__Model", model.Resource.ModelName)
    .WithEnvironment("Ollama__Embeddings", embeddings.Resource.ModelName);

builder.AddProject<Projects.Web>("web")
    .WithReference(api)
    .WithExternalHttpEndpoints();

builder.Build().Run();
