using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;
using MemoryMcp.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MemoryMcp.Core.IntegrationTests;

/// <summary>
/// Evaluation tests for the duplicate guard using synthesized personal memories.
/// Tests use imaginary persons from Canada, Argentina, China, and Australia to verify
/// that the duplicate guard correctly rejects near-identical content (paraphrases,
/// repeated extractions) while accepting genuinely distinct memories on related topics.
///
/// Requires Ollama running locally with the configured embedding model.
/// </summary>
[Trait("Category", "Integration")]
public class DuplicateGuardEvalTests : IAsyncLifetime, IDisposable
{
    private readonly ITestOutputHelper output;
    private readonly string tempDir;
    private readonly MemoryMcpOptions options;
    private readonly OllamaEmbeddingService? embeddingService;
    private readonly WordChunkingService chunkingService;
    private SqliteVecMemoryStore? store;
    private MemoryService? memoryService;
    private bool ollamaAvailable;

    // ──────────────────────────────────────────────────────────────────────────
    // Seed memories — diverse facts about imaginary persons in 4 countries.
    // Each memory covers a different life aspect so topics don't bleed together.
    // ──────────────────────────────────────────────────────────────────────────

    private static readonly (string Content, string Title, List<string> Tags)[] SeedMemories =
    [
        // ── Canada: Liam Chen, software architect in Vancouver ──
        (
            "Liam Chen is a software architect based in Vancouver, Canada. He works at a fintech startup " +
            "building real-time payment processing systems using C# and .NET. He has 12 years of experience " +
            "and previously worked at Shopify in Ottawa.",
            "Liam Chen — Career",
            ["person", "canada", "career"]
        ),
        (
            "Liam Chen prefers dark mode in all his development tools. He uses Rider as his primary IDE " +
            "with Vim keybindings. His dotfiles are version-controlled in a private GitHub repository. " +
            "He runs Arch Linux on his personal machines but uses macOS at work.",
            "Liam Chen — Dev Environment",
            ["person", "canada", "tools"]
        ),
        (
            "Liam Chen lives in a condo in Kitsilano, Vancouver with his partner and a corgi named Maple. " +
            "He enjoys trail running on the North Shore mountains and volunteers at a local coding bootcamp " +
            "on weekends teaching Python to newcomers.",
            "Liam Chen — Personal Life",
            ["person", "canada", "personal"]
        ),

        // ── Argentina: Valentina Morales, data scientist in Buenos Aires ──
        (
            "Valentina Morales is a data scientist at a biotech company in Buenos Aires, Argentina. " +
            "She specializes in genomic data analysis using Python and R. She completed her PhD in " +
            "computational biology at Universidad de Buenos Aires in 2021.",
            "Valentina Morales — Career",
            ["person", "argentina", "career"]
        ),
        (
            "Valentina Morales uses JupyterLab for interactive analysis and prefers PyCharm for production " +
            "Python code. She maintains a conda environment per project and stores all datasets on a local " +
            "NAS with ZFS for data integrity. She recently started experimenting with Polars instead of Pandas.",
            "Valentina Morales — Dev Environment",
            ["person", "argentina", "tools"]
        ),
        (
            "Valentina Morales lives in the Palermo neighborhood of Buenos Aires. She is an avid tango dancer " +
            "and performs at milongas on Friday evenings. She adopted a street cat named Malbec and enjoys " +
            "cooking traditional empanadas for friends on Sundays.",
            "Valentina Morales — Personal Life",
            ["person", "argentina", "personal"]
        ),

        // ── China: Wei Zhang, DevOps engineer in Shenzhen ──
        (
            "Wei Zhang is a DevOps engineer working at a cloud infrastructure company in Shenzhen, China. " +
            "He manages Kubernetes clusters across multiple availability zones and builds CI/CD pipelines " +
            "using GitLab CI. He has certifications in AWS and Alibaba Cloud.",
            "Wei Zhang — Career",
            ["person", "china", "career"]
        ),
        (
            "Wei Zhang uses Neovim as his primary editor with a custom Lua configuration. He runs NixOS " +
            "on his development workstation and maintains a homelab with Proxmox for testing infrastructure " +
            "changes. He prefers terminal-based workflows and uses tmux extensively.",
            "Wei Zhang — Dev Environment",
            ["person", "china", "tools"]
        ),
        (
            "Wei Zhang lives in Nanshan district, Shenzhen with his wife and two children. He practices " +
            "calligraphy on weekend mornings and coaches his daughter's school robotics team. The family " +
            "enjoys hiking in Wutong Mountain and visiting the botanical gardens in Fairy Lake.",
            "Wei Zhang — Personal Life",
            ["person", "china", "personal"]
        ),

        // ── Australia: Sarah Mitchell, game developer in Melbourne ──
        (
            "Sarah Mitchell is a game developer at an indie studio in Melbourne, Australia. She works on " +
            "procedural generation systems using Unity and C#. Before going indie, she spent 5 years at " +
            "Firaxis Games working on Civilization VI's world generation.",
            "Sarah Mitchell — Career",
            ["person", "australia", "career"]
        ),
        (
            "Sarah Mitchell uses VS Code with the Unity extension pack and has a custom snippets library " +
            "for shader programming. She runs a dual-boot Windows/Ubuntu setup — Windows for Unity builds " +
            "and Ubuntu for server-side tooling. She version-controls assets with Git LFS.",
            "Sarah Mitchell — Dev Environment",
            ["person", "australia", "tools"]
        ),
        (
            "Sarah Mitchell lives in Fitzroy, Melbourne with two rescue greyhounds named Pixel and Voxel. " +
            "She is a regular at the local board game café and organizes a monthly game jam meetup. She " +
            "surfs at Bells Beach on long weekends and collects vintage synthesizers.",
            "Sarah Mitchell — Personal Life",
            ["person", "australia", "personal"]
        ),
    ];

    // ──────────────────────────────────────────────────────────────────────────
    // Paraphrase pairs: semantically identical to a seed memory but reworded.
    // The duplicate guard SHOULD reject these.
    // ──────────────────────────────────────────────────────────────────────────

    private static readonly (string Paraphrase, string OriginalTitle)[] Paraphrases =
    [
        // Paraphrase of "Liam Chen — Career"
        (
            "Liam Chen works as a software architect in Vancouver, Canada at a fintech startup. " +
            "His focus is on real-time payment processing with C# and .NET. He has 12 years in the " +
            "industry and his prior role was at Shopify, based in Ottawa.",
            "Liam Chen — Career"
        ),
        // Paraphrase of "Valentina Morales — Personal Life"
        (
            "Valentina Morales resides in Palermo, Buenos Aires. She's a passionate tango dancer who " +
            "regularly performs at Friday milongas. She has a rescued cat called Malbec and likes to " +
            "make homemade empanadas for her friends every Sunday.",
            "Valentina Morales — Personal Life"
        ),
        // Paraphrase of "Wei Zhang — Dev Environment"
        (
            "Wei Zhang's primary code editor is Neovim, configured with Lua. His development machine " +
            "runs NixOS, and he has a Proxmox-based homelab for testing infrastructure. He relies " +
            "heavily on tmux and prefers working in the terminal.",
            "Wei Zhang — Dev Environment"
        ),
        // Paraphrase of "Sarah Mitchell — Career"
        (
            "Sarah Mitchell works at an indie game studio in Melbourne, Australia, focusing on " +
            "procedural generation in Unity with C#. She previously spent five years at Firaxis " +
            "Games contributing to Civilization VI's world generation systems.",
            "Sarah Mitchell — Career"
        ),
    ];

    // ──────────────────────────────────────────────────────────────────────────
    // Extraction-style duplicates: same info expressed as an LLM extraction.
    // The duplicate guard SHOULD reject these.
    // ──────────────────────────────────────────────────────────────────────────

    private static readonly (string Extraction, string OriginalTitle)[] Extractions =
    [
        // Extraction from a conversation mentioning Liam's dev setup
        (
            "The user mentioned that Liam Chen uses Rider IDE with Vim keybindings and prefers dark mode. " +
            "His dotfiles are on GitHub. He uses Arch Linux personally and macOS for work.",
            "Liam Chen — Dev Environment"
        ),
        // Extraction about Valentina's career
        (
            "Learned that Valentina Morales has a PhD in computational biology from Universidad de Buenos " +
            "Aires (2021). She currently works as a data scientist at a biotech firm in Buenos Aires, " +
            "specializing in genomic data analysis with Python and R.",
            "Valentina Morales — Career"
        ),
        // Extraction about Wei Zhang's personal life
        (
            "Wei Zhang lives in Nanshan district, Shenzhen. He practices calligraphy, coaches a school " +
            "robotics team for his daughter, and the family hikes Wutong Mountain. He lives with his wife " +
            "and two children.",
            "Wei Zhang — Personal Life"
        ),
        // Extraction about Sarah's personal life
        (
            "Sarah Mitchell has two rescue greyhounds — Pixel and Voxel. She lives in Fitzroy, Melbourne. " +
            "She attends a board game café, runs a monthly game jam, surfs at Bells Beach, and collects " +
            "vintage synthesizers.",
            "Sarah Mitchell — Personal Life"
        ),
    ];

    // ──────────────────────────────────────────────────────────────────────────
    // Genuinely distinct memories: related topic but different facts/people.
    // The duplicate guard SHOULD allow these.
    // ──────────────────────────────────────────────────────────────────────────

    private static readonly (string Content, string Description)[] DistinctMemories =
    [
        // Same person, genuinely new fact
        (
            "Liam Chen is allergic to shellfish and carries an EpiPen. This is important to remember " +
            "when suggesting restaurants or meal plans for him.",
            "New fact about Liam (health)"
        ),
        // Same country, different person entirely
        (
            "Marcus Thompson is a freelance photographer based in Toronto, Canada. He specializes in " +
            "architectural photography and recently published a book about brutalist buildings in Montréal.",
            "Different person, same country as Liam"
        ),
        // Same profession theme, different person and country
        (
            "Priya Sharma is a software architect in Bangalore, India. She designs microservice systems " +
            "for an e-commerce platform using Java and Kotlin. She has 15 years of experience in backend " +
            "engineering.",
            "Different architect, different country"
        ),
        // Same hobby domain, different person
        (
            "James O'Brien is a tango instructor in Dublin, Ireland. He has been teaching Argentine tango " +
            "for 10 years and runs workshops across Europe during the summer months.",
            "Different tango person, different country from Valentina"
        ),
        // Same tool (Neovim) but different person
        (
            "Akiko Tanaka uses Neovim with a heavily customized init.lua for Rust development. She is a " +
            "systems programmer at a robotics company in Tokyo and contributes to the Neovim core project.",
            "Different Neovim user, different person from Wei"
        ),
        // Same city, different person and domain
        (
            "Tom O'Reilly is a barista and coffee roaster in Melbourne, Australia. He runs a specialty " +
            "coffee shop in Brunswick and sources single-origin beans from Ethiopia and Colombia.",
            "Different person in Melbourne, unrelated domain"
        ),
        // Update to existing person: genuinely new information, not a duplicate
        (
            "Valentina Morales has been promoted to Principal Data Scientist and will now lead a team of " +
            "eight researchers. She is also starting to learn Rust for high-performance data pipelines.",
            "Genuinely new career update for Valentina"
        ),
        // Same location (Shenzhen), different domain
        (
            "Wei Zhang has started learning to play the guzheng, a traditional Chinese stringed instrument. " +
            "He takes lessons every Wednesday evening at a music school near his office in Nanshan.",
            "New hobby for Wei, distinct from existing personal life memory"
        ),
    ];

    public DuplicateGuardEvalTests(ITestOutputHelper output)
    {
        this.output = output;
        this.tempDir = Path.Combine(Path.GetTempPath(), $"memory-mcp-dedup-eval-{Guid.NewGuid():N}");
        this.options = TestOptionsHelper.CreateOptions(dataDirectory: this.tempDir);

        var optionsWrapper = Options.Create(this.options);
        this.chunkingService = new WordChunkingService(optionsWrapper);

        try
        {
            this.embeddingService = new OllamaEmbeddingService(optionsWrapper, NullLogger<OllamaEmbeddingService>.Instance);
            this.embeddingService.EmbedAsync("warmup").GetAwaiter().GetResult();
            this.ollamaAvailable = true;
            this.output.WriteLine($"Ollama connected at {this.options.Ollama.Endpoint}");
            this.output.WriteLine($"Embedding model: {this.options.Ollama.Model} ({this.options.Ollama.Dimensions} dimensions)");
            this.output.WriteLine($"Duplicate threshold: {this.options.DuplicateThreshold:F2}");
            this.output.WriteLine("");
        }
        catch (Exception ex)
        {
            this.ollamaAvailable = false;
            this.output.WriteLine($"Ollama not available: {ex.Message}");
        }
    }

    public async ValueTask InitializeAsync()
    {
        if (!this.ollamaAvailable)
        {
            return;
        }

        this.store = new SqliteVecMemoryStore(
            Options.Create(this.options),
            NullLogger<SqliteVecMemoryStore>.Instance);
        await this.store.InitializeAsync();

        this.memoryService = new MemoryService(
            this.chunkingService,
            this.embeddingService!,
            this.store,
            new StaticOptionsMonitor<MemoryMcpOptions>(this.options),
            NullLogger<MemoryService>.Instance);

        // Seed all base memories (force=true to bypass dedup guard)
        this.output.WriteLine($"Seeding {SeedMemories.Length} base memories...");
        foreach (var (content, title, tags) in SeedMemories)
        {
            var result = await this.memoryService.IngestAsync(content, title, tags, force: true);
            this.output.WriteLine($"  [{string.Join(", ", tags)}] \"{title}\" → {result.MemoryId}");
        }
        this.output.WriteLine("");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void SkipIfNoOllama()
    {
        if (!this.ollamaAvailable)
        {
            Assert.Skip("Ollama is not available. Install Ollama and pull the embedding model to run integration tests.");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test group 1: Paraphrases should be REJECTED
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Paraphrase_LiamCareer_IsRejected()
    {
        this.SkipIfNoOllama();
        var (paraphrase, originalTitle) = Paraphrases[0];
        await AssertDuplicateRejected(paraphrase, originalTitle);
    }

    [Fact]
    public async Task Paraphrase_ValentinaPersonalLife_IsRejected()
    {
        this.SkipIfNoOllama();
        var (paraphrase, originalTitle) = Paraphrases[1];
        await AssertDuplicateRejected(paraphrase, originalTitle);
    }

    [Fact]
    public async Task Paraphrase_WeiDevEnvironment_IsRejected()
    {
        this.SkipIfNoOllama();
        var (paraphrase, originalTitle) = Paraphrases[2];
        await AssertDuplicateRejected(paraphrase, originalTitle);
    }

    [Fact]
    public async Task Paraphrase_SarahCareer_IsRejected()
    {
        this.SkipIfNoOllama();
        var (paraphrase, originalTitle) = Paraphrases[3];
        await AssertDuplicateRejected(paraphrase, originalTitle);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test group 2: LLM extraction duplicates should be REJECTED
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Extraction_LiamDevSetup_IsRejected()
    {
        this.SkipIfNoOllama();
        var (extraction, originalTitle) = Extractions[0];
        await AssertDuplicateRejected(extraction, originalTitle);
    }

    [Fact]
    public async Task Extraction_ValentinaCareer_IsRejected()
    {
        this.SkipIfNoOllama();
        var (extraction, originalTitle) = Extractions[1];
        await AssertDuplicateRejected(extraction, originalTitle);
    }

    [Fact]
    public async Task Extraction_WeiPersonalLife_IsRejected()
    {
        this.SkipIfNoOllama();
        var (extraction, originalTitle) = Extractions[2];
        await AssertDuplicateRejected(extraction, originalTitle);
    }

    [Fact]
    public async Task Extraction_SarahPersonalLife_IsRejected()
    {
        this.SkipIfNoOllama();
        var (extraction, originalTitle) = Extractions[3];
        await AssertDuplicateRejected(extraction, originalTitle);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test group 3: Genuinely distinct memories should be ACCEPTED
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Distinct_LiamNewHealthFact_IsAccepted()
    {
        this.SkipIfNoOllama();
        var (content, description) = DistinctMemories[0];
        await AssertDistinctAccepted(content, description);
    }

    [Fact]
    public async Task Distinct_DifferentPersonSameCountry_IsAccepted()
    {
        this.SkipIfNoOllama();
        var (content, description) = DistinctMemories[1];
        await AssertDistinctAccepted(content, description);
    }

    [Fact]
    public async Task Distinct_SameProfessionDifferentPerson_IsAccepted()
    {
        this.SkipIfNoOllama();
        var (content, description) = DistinctMemories[2];
        await AssertDistinctAccepted(content, description);
    }

    [Fact]
    public async Task Distinct_SameHobbyDifferentPerson_IsAccepted()
    {
        this.SkipIfNoOllama();
        var (content, description) = DistinctMemories[3];
        await AssertDistinctAccepted(content, description);
    }

    [Fact]
    public async Task Distinct_SameToolDifferentPerson_IsAccepted()
    {
        this.SkipIfNoOllama();
        var (content, description) = DistinctMemories[4];
        await AssertDistinctAccepted(content, description);
    }

    [Fact]
    public async Task Distinct_SameCityDifferentDomain_IsAccepted()
    {
        this.SkipIfNoOllama();
        var (content, description) = DistinctMemories[5];
        await AssertDistinctAccepted(content, description);
    }

    [Fact]
    public async Task Distinct_ValentinaCareerUpdate_IsAccepted()
    {
        this.SkipIfNoOllama();
        var (content, description) = DistinctMemories[6];
        await AssertDistinctAccepted(content, description);
    }

    [Fact]
    public async Task Distinct_WeiNewHobby_IsAccepted()
    {
        this.SkipIfNoOllama();
        var (content, description) = DistinctMemories[7];
        await AssertDistinctAccepted(content, description);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test group 4: Force bypass should always succeed
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ForceBypass_ExactDuplicate_IsAccepted()
    {
        this.SkipIfNoOllama();

        // Use exact seed content — would normally be rejected
        var (content, title, tags) = SeedMemories[0]; // Liam Chen — Career
        this.output.WriteLine($"Force-ingesting exact copy of \"{title}\"...");

        var result = await this.memoryService!.IngestAsync(content, title, tags, force: true);

        this.output.WriteLine($"  Success: {result.Success}");
        this.output.WriteLine($"  MemoryId: {result.MemoryId}");
        this.output.WriteLine("PASS: Force bypass accepted exact duplicate");

        Assert.True(result.Success, "Force=true should always succeed, even with exact duplicates.");
        Assert.NotNull(result.MemoryId);
    }

    [Fact]
    public async Task ForceBypass_Paraphrase_IsAccepted()
    {
        this.SkipIfNoOllama();

        var (paraphrase, originalTitle) = Paraphrases[0]; // Liam career paraphrase
        this.output.WriteLine($"Force-ingesting paraphrase of \"{originalTitle}\"...");

        var result = await this.memoryService!.IngestAsync(paraphrase, force: true);

        this.output.WriteLine($"  Success: {result.Success}");
        this.output.WriteLine($"  MemoryId: {result.MemoryId}");
        this.output.WriteLine("PASS: Force bypass accepted paraphrase");

        Assert.True(result.Success, "Force=true should always succeed, even with paraphrases.");
        Assert.NotNull(result.MemoryId);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test group 5: Cross-country search quality with dedup guard
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Search_ByCountry_FindsCorrectPersons()
    {
        this.SkipIfNoOllama();

        var countryQueries = new (string Query, string Country, string[] ExpectedPersons)[]
        {
            ("people living in Canada", "Canada", ["Liam Chen"]),
            ("people from Argentina", "Argentina", ["Valentina Morales"]),
            ("people in China", "China", ["Wei Zhang"]),
            ("someone in Australia", "Australia", ["Sarah Mitchell"]),
        };

        this.output.WriteLine("Cross-country search evaluation:");
        this.output.WriteLine("─────────────────────────────────────────────────────────────");

        int passed = 0;
        foreach (var (query, country, expectedPersons) in countryQueries)
        {
            var results = await this.memoryService!.SearchAsync(query, limit: 3, tags: [country.ToLowerInvariant()]);
            var topContent = results.Count > 0 ? results[0].Content : "(no results)";
            bool found = expectedPersons.Any(p => topContent.Contains(p));
            if (found) passed++;

            this.output.WriteLine($"  Query: \"{query}\" [tag: {country.ToLowerInvariant()}]");
            for (int i = 0; i < results.Count; i++)
            {
                this.output.WriteLine($"    [{i + 1}] \"{results[i].Title}\" (score: {results[i].Score:F4})");
            }
            this.output.WriteLine($"    {(found ? "PASS" : "FAIL")}: expected one of [{string.Join(", ", expectedPersons)}]");
        }

        this.output.WriteLine("─────────────────────────────────────────────────────────────");
        this.output.WriteLine($"Country search accuracy: {passed}/{countryQueries.Length}");

        Assert.True(passed >= 3, $"Expected at least 3/4 country queries to find the right person, got {passed}/4.");
    }

    [Fact]
    public async Task Search_ByTopic_FindsCorrectAspect()
    {
        this.SkipIfNoOllama();

        var topicQueries = new (string Query, string ExpectedTitle)[]
        {
            ("What IDE does Liam use?", "Liam Chen — Dev Environment"),
            ("Where does Valentina live and what are her hobbies?", "Valentina Morales — Personal Life"),
            ("What Kubernetes certifications does Wei have?", "Wei Zhang — Career"),
            ("What games did Sarah work on?", "Sarah Mitchell — Career"),
        };

        this.output.WriteLine("Topic search evaluation:");
        this.output.WriteLine("─────────────────────────────────────────────────────────────");

        int correctAtK1 = 0;
        foreach (var (query, expectedTitle) in topicQueries)
        {
            var results = await this.memoryService!.SearchAsync(query, limit: 3);
            bool correct = results.Count > 0 && results[0].Title == expectedTitle;
            if (correct) correctAtK1++;

            this.output.WriteLine($"  Query: \"{query}\"");
            for (int i = 0; i < results.Count; i++)
            {
                string marker = results[i].Title == expectedTitle ? " <-- TARGET" : "";
                this.output.WriteLine($"    [{i + 1}] \"{results[i].Title}\" (score: {results[i].Score:F4}){marker}");
            }
            this.output.WriteLine($"    {(correct ? "CORRECT" : "WRONG")}");
        }

        float precision = (float)correctAtK1 / topicQueries.Length;
        this.output.WriteLine("─────────────────────────────────────────────────────────────");
        this.output.WriteLine($"Precision@1: {precision:F2} ({correctAtK1}/{topicQueries.Length}) — threshold: >= 0.75");

        Assert.True(precision >= 0.75f,
            $"Topic precision@1 = {precision:F2} ({correctAtK1}/{topicQueries.Length}). Expected >= 0.75");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test group 6: Aggregate precision/recall for the duplicate guard
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DuplicateGuard_AggregateEval_ParaphraseRejectionRate()
    {
        this.SkipIfNoOllama();

        this.output.WriteLine("Aggregate paraphrase rejection evaluation:");
        this.output.WriteLine("─────────────────────────────────────────────────────────────");

        int rejected = 0;
        foreach (var (paraphrase, originalTitle) in Paraphrases)
        {
            var result = await this.memoryService!.IngestAsync(paraphrase);
            bool wasRejected = !result.Success;
            if (wasRejected) rejected++;

            var topMatch = result.SimilarMemories.Count > 0 ? result.SimilarMemories[0] : null;
            this.output.WriteLine($"  Paraphrase of \"{originalTitle}\":");
            this.output.WriteLine($"    Rejected: {wasRejected}");
            if (topMatch != null)
            {
                this.output.WriteLine($"    Top match: \"{topMatch.Title}\" (score: {topMatch.Score:F4})");
                this.output.WriteLine($"    Correct match: {topMatch.Title == originalTitle}");
            }
        }

        float rejectionRate = (float)rejected / Paraphrases.Length;
        this.output.WriteLine("─────────────────────────────────────────────────────────────");
        this.output.WriteLine($"Paraphrase rejection rate: {rejectionRate:F2} ({rejected}/{Paraphrases.Length}) — threshold: >= 0.75");

        Assert.True(rejectionRate >= 0.75f,
            $"Paraphrase rejection rate = {rejectionRate:F2} ({rejected}/{Paraphrases.Length}). Expected >= 0.75");
    }

    [Fact]
    public async Task DuplicateGuard_AggregateEval_ExtractionRejectionRate()
    {
        this.SkipIfNoOllama();

        this.output.WriteLine("Aggregate extraction rejection evaluation:");
        this.output.WriteLine("─────────────────────────────────────────────────────────────");

        int rejected = 0;
        foreach (var (extraction, originalTitle) in Extractions)
        {
            var result = await this.memoryService!.IngestAsync(extraction);
            bool wasRejected = !result.Success;
            if (wasRejected) rejected++;

            var topMatch = result.SimilarMemories.Count > 0 ? result.SimilarMemories[0] : null;
            this.output.WriteLine($"  Extraction matching \"{originalTitle}\":");
            this.output.WriteLine($"    Rejected: {wasRejected}");
            if (topMatch != null)
            {
                this.output.WriteLine($"    Top match: \"{topMatch.Title}\" (score: {topMatch.Score:F4})");
                this.output.WriteLine($"    Correct match: {topMatch.Title == originalTitle}");
            }
        }

        float rejectionRate = (float)rejected / Extractions.Length;
        this.output.WriteLine("─────────────────────────────────────────────────────────────");
        this.output.WriteLine($"Extraction rejection rate: {rejectionRate:F2} ({rejected}/{Extractions.Length}) — threshold: >= 0.50");

        // Lower threshold for extractions — they are less verbatim than paraphrases
        Assert.True(rejectionRate >= 0.50f,
            $"Extraction rejection rate = {rejectionRate:F2} ({rejected}/{Extractions.Length}). Expected >= 0.50");
    }

    [Fact]
    public async Task DuplicateGuard_AggregateEval_DistinctAcceptanceRate()
    {
        this.SkipIfNoOllama();

        this.output.WriteLine("Aggregate distinct memory acceptance evaluation:");
        this.output.WriteLine("─────────────────────────────────────────────────────────────");

        int accepted = 0;
        foreach (var (content, description) in DistinctMemories)
        {
            var result = await this.memoryService!.IngestAsync(content);
            bool wasAccepted = result.Success;
            if (wasAccepted) accepted++;

            this.output.WriteLine($"  \"{description}\":");
            this.output.WriteLine($"    Accepted: {wasAccepted}");
            if (!wasAccepted && result.SimilarMemories.Count > 0)
            {
                var topMatch = result.SimilarMemories[0];
                this.output.WriteLine($"    False positive — matched \"{topMatch.Title}\" (score: {topMatch.Score:F4})");
            }
        }

        float acceptanceRate = (float)accepted / DistinctMemories.Length;
        this.output.WriteLine("─────────────────────────────────────────────────────────────");
        this.output.WriteLine($"Distinct acceptance rate: {acceptanceRate:F2} ({accepted}/{DistinctMemories.Length}) — threshold: >= 0.75");

        Assert.True(acceptanceRate >= 0.75f,
            $"Distinct acceptance rate = {acceptanceRate:F2} ({accepted}/{DistinctMemories.Length}). Expected >= 0.75");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Test group 7: Edge cases
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EdgeCase_ExactDuplicate_IsRejected()
    {
        this.SkipIfNoOllama();

        // Ingest the exact same content as a seed memory (verbatim copy)
        var (content, title, _) = SeedMemories[0]; // Liam Chen — Career
        this.output.WriteLine($"Ingesting exact copy of \"{title}\"...");

        var result = await this.memoryService!.IngestAsync(content);

        this.output.WriteLine($"  Success: {result.Success}");
        this.output.WriteLine($"  Similar memories: {result.SimilarMemories.Count}");
        if (result.SimilarMemories.Count > 0)
        {
            foreach (var sm in result.SimilarMemories)
            {
                this.output.WriteLine($"    \"{sm.Title}\" (score: {sm.Score:F4})");
            }
        }

        Assert.False(result.Success, "Exact duplicate should be rejected.");
        Assert.NotEmpty(result.SimilarMemories);

        // The top match should be the exact original
        Assert.Equal(title, result.SimilarMemories[0].Title);
    }

    [Fact]
    public async Task EdgeCase_SamePersonDifferentAspect_IsAccepted()
    {
        this.SkipIfNoOllama();

        // Liam's career is seeded; this is about his cooking hobby — completely different aspect
        var content = "Liam Chen has been learning to bake sourdough bread during the pandemic. He maintains " +
                      "a 3-year-old starter named 'Compile' and bakes two loaves every Saturday. His specialty " +
                      "is a rosemary olive oil loaf that he brings to the office.";
        this.output.WriteLine("Ingesting new aspect for Liam (cooking hobby)...");

        var result = await this.memoryService!.IngestAsync(content);

        this.output.WriteLine($"  Success: {result.Success}");
        if (!result.Success && result.SimilarMemories.Count > 0)
        {
            this.output.WriteLine($"  False positive — matched \"{result.SimilarMemories[0].Title}\" (score: {result.SimilarMemories[0].Score:F4})");
        }

        Assert.True(result.Success, "A genuinely new aspect about the same person should be accepted.");
    }

    [Fact]
    public async Task EdgeCase_ShortOverlappingFact_IsAccepted()
    {
        this.SkipIfNoOllama();

        // Short factoid that mentions Wei Zhang but is about a completely different topic
        var content = "Wei Zhang's favorite programming language is Go. He uses it for all his CLI tooling " +
                      "and personal automation scripts.";
        this.output.WriteLine("Ingesting short new factoid about Wei Zhang...");

        var result = await this.memoryService!.IngestAsync(content);

        this.output.WriteLine($"  Success: {result.Success}");
        if (!result.Success && result.SimilarMemories.Count > 0)
        {
            this.output.WriteLine($"  False positive — matched \"{result.SimilarMemories[0].Title}\" (score: {result.SimilarMemories[0].Score:F4})");
        }

        Assert.True(result.Success, "A short new fact about an existing person should be accepted.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Helper methods
    // ══════════════════════════════════════════════════════════════════════════

    private async Task AssertDuplicateRejected(string candidateContent, string expectedOriginalTitle)
    {
        this.output.WriteLine($"Testing duplicate detection against \"{expectedOriginalTitle}\"...");
        this.output.WriteLine($"  Candidate: \"{candidateContent[..Math.Min(80, candidateContent.Length)]}...\"");

        var result = await this.memoryService!.IngestAsync(candidateContent);

        this.output.WriteLine($"  Success: {result.Success}");
        this.output.WriteLine($"  Similar memories: {result.SimilarMemories.Count}");
        if (result.SimilarMemories.Count > 0)
        {
            foreach (var sm in result.SimilarMemories)
            {
                this.output.WriteLine($"    \"{sm.Title}\" (score: {sm.Score:F4})");
            }
        }

        Assert.False(result.Success, $"Expected duplicate to be rejected for \"{expectedOriginalTitle}\".");
        Assert.NotEmpty(result.SimilarMemories);

        // Verify the correct original memory was identified as the match
        bool correctMatch = result.SimilarMemories.Any(sm => sm.Title == expectedOriginalTitle);
        this.output.WriteLine($"  Correct match found: {correctMatch}");
        this.output.WriteLine(correctMatch ? "PASS" : "FAIL");

        Assert.True(correctMatch,
            $"Expected similar memories to include \"{expectedOriginalTitle}\", but got: " +
            $"[{string.Join(", ", result.SimilarMemories.Select(sm => $"\"{sm.Title}\" ({sm.Score:F4})"))}]");
    }

    private async Task AssertDistinctAccepted(string content, string description)
    {
        this.output.WriteLine($"Testing distinct memory acceptance: \"{description}\"...");
        this.output.WriteLine($"  Content: \"{content[..Math.Min(80, content.Length)]}...\"");

        var result = await this.memoryService!.IngestAsync(content);

        this.output.WriteLine($"  Success: {result.Success}");
        if (!result.Success)
        {
            this.output.WriteLine($"  Rejection reason: {result.RejectionReason}");
            foreach (var sm in result.SimilarMemories)
            {
                this.output.WriteLine($"    False positive: \"{sm.Title}\" (score: {sm.Score:F4})");
            }
        }
        else
        {
            this.output.WriteLine($"  MemoryId: {result.MemoryId}");
        }
        this.output.WriteLine(result.Success ? "PASS" : "FAIL");

        Assert.True(result.Success, $"Expected distinct memory to be accepted: \"{description}\".");
    }

    public void Dispose()
    {
        this.store?.Dispose();
        this.embeddingService?.Dispose();

        try
        {
            if (Directory.Exists(this.tempDir))
            {
                Directory.Delete(this.tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
