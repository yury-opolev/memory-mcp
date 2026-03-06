# Embedding Model Research

Research findings from benchmarking embedding models for use with Memory MCP.
All models were tested via Ollama on Windows with an NVIDIA GPU.

## Summary

| Model | Dims | Size | Architecture | Test Results | Verdict |
|-------|------|------|-------------|-------------|---------|
| **qwen3-embedding:0.6b** | 1024 | ~639 MB (Q8_0) | Qwen3 (decoder) | **27/27 passed** | **Default. Recommended.** |
| nomic-embed-text | 768 | ~274 MB | NomicBERT | 26/27 passed | Usable, lower similarity scores |
| bge-m3 | 1024 | ~1.2 GB (F16) | XLM-RoBERTa (BERT) | 24/27 passed | Not recommended via Ollama (NaN bug) |

## qwen3-embedding:0.6b (Default)

- **HuggingFace:** [Qwen/Qwen3-Embedding-0.6B](https://huggingface.co/Qwen/Qwen3-Embedding-0.6B)
- **Parameters:** 0.6B
- **Context length:** 32K tokens
- **Embedding dimensions:** Up to 1024 (supports custom output dimensions 32-1024)
- **Architecture:** Qwen3 decoder-based transformer
- **License:** Apache 2.0
- **Languages:** 100+

### Test Results

- **27/27 integration tests passed** consistently
- Good cosine similarity scores across all quality tests
- Paraphrase detection similarity > 0.7
- Embedding latency: ~66ms single text, ~161ms batch of 10 (~62 embeddings/sec)
- No issues with concurrent/parallel requests
- No NaN or numerical stability issues

### Why It's the Default

1. Perfect pass rate on all quality and performance tests
2. Small footprint (~639 MB) with good quality
3. Decoder architecture avoids the llama.cpp BERT NaN bug (see bge-m3 section)
4. Fast inference, good for interactive use
5. Supports 100+ languages and 32K context
6. No ONNX export available, but not needed since Ollama works perfectly

---

## nomic-embed-text

- **HuggingFace:** [nomic-ai/nomic-embed-text-v1.5](https://huggingface.co/nomic-ai/nomic-embed-text-v1.5)
- **Parameters:** ~137M
- **Context length:** 8K tokens
- **Embedding dimensions:** 768 (supports Matryoshka dimensions down to 64)
- **Architecture:** NomicBERT (custom BERT variant)
- **License:** Apache 2.0
- **Languages:** English primarily

### Test Results

- **26/27 integration tests passed, 1 failed**
- Failed test: `SameMeaning_DifferentPhrasing_HighSimilarity` -- cosine similarity was 0.5868, below the 0.7 threshold
- All search quality tests passed (relative ranking works, absolute similarity scores are lower)
- Would need similarity threshold lowered to ~0.55 to pass all tests with this model

### Assessment

Usable as a lighter alternative if 768 dimensions and lower absolute similarity scores are acceptable.
The relative ranking of search results is correct, so search quality is fine in practice.
Requires `search_document:` and `search_query:` task prefixes for optimal performance
(our code does not currently add these).
ONNX export is available on HuggingFace.

### Configuration

```json
{
  "MemoryMcp": {
    "Ollama": {
      "Model": "nomic-embed-text",
      "Dimensions": 768
    }
  }
}
```

Or via environment variables:

```bash
export MEMORYMCP_OLLAMA_MODEL=nomic-embed-text
export MEMORYMCP_OLLAMA_DIMENSIONS=768
```

---

## bge-m3

- **HuggingFace:** [BAAI/bge-m3](https://huggingface.co/BAAI/bge-m3)
- **Parameters:** 567M
- **Context length:** 8K tokens
- **Embedding dimensions:** 1024
- **Architecture:** XLM-RoBERTa (BERT-family encoder)
- **License:** MIT
- **Languages:** 100+ (strong multilingual and cross-lingual performance)

### Test Results

- **24/27 integration tests passed, 3 failed**
- All 3 failures are HTTP 500 errors from Ollama, NOT quality failures
- Failed tests: `DissimilarTexts_HaveLowCosineSimilarity`, `SearchLatency_WithPopulatedStore`, `IngestMemory_ShortContent_CompletesQuickly`
- Tests that do pass show good quality -- search ranking and similarity thresholds are met

### The NaN Bug (Known Ollama/llama.cpp Issue)

bge-m3 produces **NaN (Not a Number) values** in its embedding output for certain input texts
when run through Ollama. This is a known bug in llama.cpp's BERT embedding implementation
that causes numerical instability for specific inputs.

**Key findings from our testing:**

- The error message is: `"failed to encode response: json: unsupported value: NaN"`
- It is **NOT a concurrency issue** -- single isolated requests also fail for affected texts
- It is **input-dependent** -- certain texts consistently produce NaN, others work fine
- Approximately 5-6% of texts are affected (one reporter saw 76/1217 texts fail)
- No detectable pattern in which texts trigger the bug
- The same model works perfectly with other inference backends (HuggingFace Transformers, ONNX Runtime)

**Root cause:** The bug is in llama.cpp's BERT/encoder model inference code. Ollama, llama-server,
and LLamaSharp all share this same C/C++ code path, so switching between llama.cpp-based tools
does not fix the issue.

**Relevant Ollama GitHub issues:**

| Issue | Date | Status | Description |
|-------|------|--------|-------------|
| [#13572](https://github.com/ollama/ollama/issues/13572) | Dec 2025 | Closed ([PR #13599](https://github.com/ollama/ollama/pull/13599)) | bge-m3 NaN on ~6% of texts. Fix only improved error message, did NOT fix root cause. |
| [#9639](https://github.com/ollama/ollama/issues/9639) | Mar 2025 | Open (assigned) | NaN with nomic-embed-text and bge-m3 |
| [#14657](https://github.com/ollama/ollama/issues/14657) | Mar 2026 | Open | bge-m3 NaN on Bitcoin whitepaper, technical docs. Still present in Ollama 0.17.6. |
| [#13643](https://github.com/ollama/ollama/issues/13643) | Jan 2026 | Open | Input character limit related NaN |

### Assessment

**Not recommended for use via Ollama** due to the NaN bug. The bug has been open for over a year
with no fix for the underlying numerical instability.

If bge-m3 is needed (e.g., for its strong multilingual capabilities), consider running it via
**ONNX Runtime** instead of Ollama. An official ONNX export (~2.27 GB) is available in the
HuggingFace repository. This would require implementing an alternative `IEmbeddingService`
backend -- the `IEmbeddingService` interface is already designed to support swappable backends.

### Configuration (if used despite the NaN risk)

```json
{
  "MemoryMcp": {
    "Ollama": {
      "Model": "bge-m3",
      "Dimensions": 1024
    }
  }
}
```

---

## Other Models Considered

### snowflake-arctic-embed (1024 dims, ~669 MB)

Not tested. Has a **512-token context window**, which is too short for our default 512-word
chunks (~665 tokens). Would require reducing chunk size. Not recommended.

---

## ONNX Runtime as Alternative Backend

If a model has issues via Ollama (like bge-m3's NaN bug), ONNX Runtime is the most natural
alternative for a C# project:

- First-class .NET support via `Microsoft.ML.OnnxRuntime` NuGet package
- Runs in-process, no external server needed
- GPU support via CUDA/DirectML providers
- Uses a completely independent inference implementation from llama.cpp

| Model | ONNX Available? | Notes |
|-------|-----------------|-------|
| bge-m3 | Yes | Official export in HuggingFace repo (`onnx/` folder) |
| nomic-embed-text-v1.5 | Yes | Available in HuggingFace repo |
| Qwen3-Embedding-0.6B | No | Only Safetensors/PyTorch. Conversion possible but non-trivial (decoder architecture). |

The existing `IEmbeddingService` interface is designed to support multiple backends.
An ONNX-based implementation could be added alongside `OllamaEmbeddingService` if needed.

---

## Background: Ollama and llama.cpp

Ollama is built on top of a **fork of llama.cpp**. The Go-based Ollama server handles
API routing, model management, and HTTP, while the actual tensor math and model inference
happens in the underlying C/C++ llama.cpp code. Models are stored in GGUF format
(llama.cpp's native format).

This is why the bge-m3 NaN bug affects all llama.cpp-based tools equally:
- **Decoder models** (like Qwen3) use llama.cpp's well-tested decoder path
- **BERT/encoder models** (like bge-m3) use a newer, less mature encoder path that has numerical stability issues
