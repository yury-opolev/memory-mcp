using System.ComponentModel;
using System.Text;
using System.Text.Json;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace MemoryMcp.Tools;

/// <summary>
/// MCP tool definitions for the memory service.
/// Each method is a tool exposed to MCP clients.
/// </summary>
[McpServerToolType]
public class MemoryTools
{
    private readonly IMemoryService memoryService;
    private readonly MemoryMcpOptions options;
    private readonly ILogger<MemoryTools> logger;

    public MemoryTools(IMemoryService memoryService, IOptions<MemoryMcpOptions> options, ILogger<MemoryTools> logger)
    {
        this.memoryService = memoryService;
        this.options = options.Value;
        this.logger = logger;
    }

    [McpServerTool(Name = "ingest_memory")]
    [Description("Store a new memory. The content will be chunked, embedded, and indexed for semantic search. " +
        "If near-duplicate memories are detected, the ingest is rejected with details about the similar memories. " +
        "Use force=true to bypass the duplicate check.")]
    public async Task<string> IngestMemory(
        [Description("The full text content to store as a memory.")] string content,
        [Description("Optional short title or label for the memory.")] string? title = null,
        [Description("Optional tags for categorization, as a JSON array of strings (e.g. [\"project\",\"notes\"]).")] string? tags = null,
        [Description("If true, bypass the duplicate check and always store the memory.")] bool force = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tagList = ParseTags(tags);
            var result = await this.memoryService.IngestAsync(content, title, tagList, force, cancellationToken);

            if (result.Success)
            {
                return $"Memory stored successfully. ID: {result.MemoryId}";
            }

            // Duplicate guard rejected the ingest — format similar memories for the caller
            var sb = new StringBuilder();
            sb.AppendLine(result.RejectionReason);
            sb.AppendLine();

            foreach (var similar in result.SimilarMemories)
            {
                sb.AppendLine($"--- Similar Memory: {similar.MemoryId} (Score: {similar.Score:F4}) ---");
                if (similar.Title is not null)
                {
                    sb.AppendLine($"Title: {similar.Title}");
                }
                sb.AppendLine(similar.Content);
                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch (InvalidOperationException ex) when (ex.InnerException is HttpRequestException or not null)
        {
            this.logger.LogError(ex, "Ollama error during ingest_memory.");
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "get_memory")]
    [Description("Retrieve a memory by its ID, including full content, title, tags, and timestamps.")]
    public async Task<string> GetMemory(
        [Description("The memory ID (GUID) to retrieve.")] string id,
        CancellationToken cancellationToken = default)
    {
        var result = await this.memoryService.GetAsync(id, cancellationToken);
        if (result is null)
        {
            return $"Memory not found: {id}";
        }

        return FormatMemoryResult(result);
    }

    [McpServerTool(Name = "update_memory")]
    [Description("Update an existing memory. If content is provided, it will be re-chunked and re-embedded. Title and tags can be updated independently.")]
    public async Task<string> UpdateMemory(
        [Description("The memory ID (GUID) to update.")] string id,
        [Description("New content text. If provided, the memory will be re-chunked and re-embedded.")] string? content = null,
        [Description("New title for the memory.")] string? title = null,
        [Description("New tags as a JSON array of strings (e.g. [\"project\",\"notes\"]).")] string? tags = null,
        CancellationToken cancellationToken = default)
    {
        if (content is null && title is null && tags is null)
        {
            return "No updates provided. Specify at least one of: content, title, or tags.";
        }

        try
        {
            var tagList = ParseTags(tags);
            var result = await this.memoryService.UpdateAsync(id, content, title, tagList, cancellationToken);
            if (result is null)
            {
                return $"Memory not found: {id}";
            }

            return $"Memory updated successfully.\n\n{FormatMemoryResult(result)}";
        }
        catch (InvalidOperationException ex) when (ex.InnerException is HttpRequestException or not null)
        {
            this.logger.LogError(ex, "Ollama error during update_memory.");
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "delete_memory")]
    [Description("Delete a memory and all its chunks permanently.")]
    public async Task<string> DeleteMemory(
        [Description("The memory ID (GUID) to delete.")] string id,
        CancellationToken cancellationToken = default)
    {
        var deleted = await this.memoryService.DeleteAsync(id, cancellationToken);
        return deleted
            ? $"Memory {id} deleted successfully."
            : $"Memory not found: {id}";
    }

    [McpServerTool(Name = "search_memory")]
    [Description("Search memories by semantic similarity. Returns the most relevant memories ranked by similarity score.")]
    public async Task<string> SearchMemory(
        [Description("The search query text. Memories semantically similar to this text will be returned.")] string query,
        [Description("Maximum number of results to return (default: 5).")] int limit = 5,
        [Description("Minimum similarity score threshold, 0.0 to 1.0 (optional).")] float? minScore = null,
        [Description("Filter by tags: only return memories with at least one matching tag. JSON array of strings (e.g. [\"project\"]).")] string? tags = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tagList = ParseTags(tags);
            var results = await this.memoryService.SearchAsync(query, limit, minScore, tagList, cancellationToken);

            if (results.Count == 0)
            {
                return "No matching memories found.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Found {results.Count} matching memories:\n");

            foreach (var result in results)
            {
                sb.AppendLine($"--- Memory: {result.MemoryId} (Score: {result.Score:F4}) ---");
                if (result.Title is not null)
                {
                    sb.AppendLine($"Title: {result.Title}");
                }
                if (result.Tags.Count > 0)
                {
                    sb.AppendLine($"Tags: {string.Join(", ", result.Tags)}");
                }
                sb.AppendLine($"Created: {result.CreatedAt:u} | Updated: {result.UpdatedAt:u}");

                // Truncate content if needed
                var resultContent = result.Content;
                if (resultContent.Length > this.options.SearchMaxContentLength)
                {
                    resultContent = resultContent[..this.options.SearchMaxContentLength];
                    sb.AppendLine(resultContent);
                    sb.AppendLine($"[truncated] Use get_memory with id \"{result.MemoryId}\" to read full content.");
                }
                else
                {
                    sb.AppendLine(resultContent);
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch (InvalidOperationException ex) when (ex.InnerException is HttpRequestException or not null)
        {
            this.logger.LogError(ex, "Ollama error during search_memory.");
            return $"Error: {ex.Message}";
        }
    }

    private static List<string>? ParseTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(tagsJson);
        }
        catch (JsonException)
        {
            // If not valid JSON array, treat as a single tag
            return [tagsJson];
        }
    }

    private static string FormatMemoryResult(Core.Models.MemoryResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ID: {result.MemoryId}");
        if (result.Title is not null)
        {
            sb.AppendLine($"Title: {result.Title}");
        }
        if (result.Tags.Count > 0)
        {
            sb.AppendLine($"Tags: {string.Join(", ", result.Tags)}");
        }
        sb.AppendLine($"Created: {result.CreatedAt:u}");
        sb.AppendLine($"Updated: {result.UpdatedAt:u}");
        sb.AppendLine($"Content:\n{result.Content}");
        return sb.ToString();
    }
}
