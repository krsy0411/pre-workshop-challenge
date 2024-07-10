using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using Aliencube.YouTubeSubtitlesExtractor;
using Aliencube.YouTubeSubtitlesExtractor.Abstractions;
using Aliencube.YouTubeSubtitlesExtractor.Models;

using AspireYouTubeSummariser.ApiApp.Plugins.AddMemory;

using Azure;
using Azure.AI.OpenAI;

using MelonChart.Models;

using Microsoft.AspNetCore.Mvc;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Memory;

var builder = WebApplication.CreateBuilder(args);

// builder.AddServiceDefaults();

builder.Services.AddHttpClient<IYouTubeVideo, YouTubeVideo>("youtube");
builder.Services.AddScoped<OpenAIClient>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var endpoint = new Uri(config["OpenAI:Endpoint"]);
    var credential = new AzureKeyCredential(config["OpenAI:ApiKey"]);
    var client = new OpenAIClient(endpoint, credential);

    return client;
});

builder.Services.AddScoped<YouTubeSummariserService>();

builder.Services.AddScoped<Kernel>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var kernel = Kernel.CreateBuilder()
                       .AddAzureOpenAIChatCompletion(
                           deploymentName: config["OpenAI:DeploymentName"],
                           endpoint: config["OpenAI:Endpoint"],
                           apiKey: config["OpenAI:ApiKey"])
                       .Build();

    kernel.ImportPluginFromType<AddMelonChartPlugin>();

    return kernel;
});

#pragma warning disable SKEXP0001
#pragma warning disable SKEXP0010
#pragma warning disable SKEXP0050
builder.Services.AddSingleton<ISemanticTextMemory>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var memory = new MemoryBuilder()
                     .WithAzureOpenAITextEmbeddingGeneration(
                         deploymentName: "model-textembeddingada002-2",
                         endpoint: config["OpenAI:Endpoint"],
                         apiKey: config["OpenAI:ApiKey"])
                     .WithMemoryStore(new VolatileMemoryStore())
                     .Build();

    return memory;
});
#pragma warning restore SKEXP0050
#pragma warning restore SKEXP0010
#pragma warning restore SKEXP0001

var jso = new JsonSerializerOptions()
{
    WriteIndented = false,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
};
builder.Services.AddSingleton(jso);

builder.Services.AddHttpClient<MelonChartService>("memory");

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

if (app.Environment.IsDevelopment())
{
    app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
}

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast = Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast")
.WithOpenApi();

app.MapPost("/summarise", async ([FromBody] SummaryRequest req, YouTubeSummariserService service) =>
{
    var summary = await service.SummariseAsync(req);
    return summary;
})
.WithName("GetSummary")
.WithOpenApi();

app.MapPost("/melonchart", async ([FromBody] MelonChartRequest req, MelonChartService service) =>
{
    var summary = await service.SummariseAsync(req);
    return summary;
})
.WithName("GetMelonChartSummary")
.WithOpenApi();

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

record SummaryRequest(string? YouTubeLinkUrl, string VideoLanguageCode, string? SummaryLanguageCode);
record MelonChartRequest(string? Question);

#pragma warning disable SKEXP0001

internal class MelonChartService(HttpClient http, Kernel kernel, ISemanticTextMemory memory, JsonSerializerOptions jso)
{
    public async Task<string> SummariseAsync(MelonChartRequest req)
    {
        // Thread.Sleep(30000);

        var prompts = kernel.ImportPluginFromPromptDirectory("Prompts");
        var getIntent = prompts["GetIntent"];
        var refineQuestion = prompts["RefineQuestion"];
        var refineResult = prompts["RefineResult"];

        var intent = await kernel.InvokeAsync<string>(
                                function: getIntent,
                                arguments: new KernelArguments()
                                {
                                    { "input", req.Question }
                                });

        Console.WriteLine($"Intent: {intent}");

        var refined = await kernel.InvokeAsync<string>(
                        function: refineQuestion,
                        arguments: new KernelArguments()
                        {
                            { "input", req.Question },
                            { "intent", intent }
                        });

        Console.WriteLine($"Refined Question: {refined}");

        await kernel.InvokeAsync(
            pluginName: nameof(AddMelonChartPlugin),
            functionName: nameof(AddMelonChartPlugin.AddChart),
            arguments: new KernelArguments()
            {
                { "memory", memory },
                { "http", http },
                { "jso", jso },
            }
        );

        var results = await kernel.InvokeAsync(
            pluginName: nameof(AddMelonChartPlugin),
            functionName: nameof(AddMelonChartPlugin.FindSongs),
            arguments: new KernelArguments()
            {
                { "memory", memory },
                { "question", refined },
                { "jso", jso },
            }
        );

        var items = results.GetValue<List<ChartItem>>();
        if (items.Any() == false)
        {
            return "no result found";
        }

        var data = results.GetValue<List<ChartItem>>()?.Select(p => JsonSerializer.Serialize(p, jso)).Aggregate((x, y) => $"{x}\n{y}");
        Console.WriteLine(data);

        refined = await kernel.InvokeAsync<string>(
                        function: refineResult,
                        arguments: new KernelArguments()
                        {
                            { "input", data },
                            { "intent", intent }
                        });

        Console.WriteLine($"Refined Result: {refined}");

        return refined;
    }
}

#pragma warning restore SKEXP0001

internal class YouTubeSummariserService(IYouTubeVideo youtube, OpenAIClient openai, IConfiguration config)
{
    private readonly IYouTubeVideo _youtube = youtube ?? throw new ArgumentNullException(nameof(youtube));
    private readonly OpenAIClient _openai = openai ?? throw new ArgumentNullException(nameof(openai));
    private readonly IConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));

    public async Task<string> SummariseAsync(SummaryRequest req)
    {
        Subtitle subtitle = await this._youtube.ExtractSubtitleAsync(req.YouTubeLinkUrl, req.VideoLanguageCode).ConfigureAwait(false);
        string caption = subtitle.Content.Select(p => p.Text).Aggregate((a, b) => $"{a}\n{b}");

        //var chat = this._openai.GetChatClient(this._config["OpenAI:DeploymentName"]);
        //var messages = new List<ChatRequestMessage>()
        //{
        //    new SystemRequestChatMessage(this._config["Prompt:System"]),
        //    new SystemChatMessage($"Here's the transcript. Summarise it in 5 bullet point items in the given language code of \"{req.SummaryLanguageCode}\"."),
        //    new UserChatMessage(caption),
        //};
        ChatCompletionsOptions options = new()
        {
            DeploymentName = this._config["OpenAI:DeploymentName"],

            MaxTokens = int.TryParse(this._config["Prompt:MaxTokens"], out var maxTokens) ? maxTokens : 3000,
            Temperature = float.TryParse(this._config["Prompt:Temperature"], out var temperature) ? temperature : 0.7f,

            Messages = {
                           new ChatRequestSystemMessage(this._config["Prompt:System"]),
                           new ChatRequestSystemMessage($"Here's the transcript. Summarise it in 5 bullet point items in the given language code of \"{req.SummaryLanguageCode}\"."),
                           new ChatRequestUserMessage(caption),
                       }
        };

        var response = await this._openai.GetChatCompletionsAsync(options).ConfigureAwait(false);
        var summary = response.Value.Choices[0].Message.Content;
        //var response = await chat.CompleteChatAsync(messages, options).ConfigureAwait(false);
        //var summary = response.Value.Content[0].Text;

        return summary;
    }
}