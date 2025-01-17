using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

using MelonChart.Models;

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Memory;

namespace AspireYouTubeSummariser.ApiApp.Plugins.AddMemory;

#pragma warning disable SKEXP0001

public class AddMelonChartPlugin
{
    private const string COLLECTION = "MelonChart";

    [KernelFunction, Description("Add Melon Chart data to the memory")]
    public static async Task AddChart(
        [Description("The Semantic Memory instance")] ISemanticTextMemory memory,
        [Description("The HttpClient instance")] HttpClient http,
        [Description("The JsonSerializerOptions instance")] JsonSerializerOptions jso)
    {
        var timezone = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Korea Standard Time" : "Asia/Seoul";
        var today = DateTimeOffset.UtcNow
                                .ToOffset(TimeZoneInfo.FindSystemTimeZoneById(timezone).BaseUtcOffset)
                                .ToString("yyyyMMdd");

        var data = await http.GetStringAsync($"https://raw.githubusercontent.com/aliencube/MelonChart.NET/main/data/top100-{20240707}.json");
        var chart = JsonSerializer.Deserialize<ChartItemCollection>(data, jso);

        foreach (var item in chart.Items)
        {
            var index = chart.Items.IndexOf(item) + 1;
            var serialised = JsonSerializer.Serialize(item, jso);
            await memory.SaveInformationAsync(collection: COLLECTION, id: $"{today}-{index.ToString("000")}", text: serialised);

            Console.WriteLine($"- Stored: {item.Artist} - {item.Title}");
        }
    }

    [KernelFunction, Description("Search question from the memory")]
    public static async Task<List<ChartItem>> FindSongs(
        [Description("The Semantic Memory instance")] ISemanticTextMemory memory,
        [Description("The question")] string question,
        [Description("The JsonSerializerOptions instance")] JsonSerializerOptions jso)
    {
        var results = await memory.SearchAsync(COLLECTION, question, limit: 100, minRelevanceScore: 0.8d).ToListAsync();
        var output = results.Select(r => JsonSerializer.Deserialize<ChartItem>(r.Metadata.Text, jso)).ToList();

        return output;
    }
}