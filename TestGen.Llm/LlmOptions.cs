using System.ComponentModel.DataAnnotations;

namespace TestGen.Llm;

public sealed class LlmOptions
{
    public const string SectionName = "Llm";
    public const string HttpClientName = "Llm";

    [Required(ErrorMessage = "Llm:BaseUrl is required (e.g. http://localhost:11434/v1/).")]
    [Url(ErrorMessage = "Llm:BaseUrl must be an absolute http(s) URI.")]
    public string BaseUrl { get; set; } = "";

    [Required(ErrorMessage = "Llm:Model is required (e.g. qwen2.5-coder:14b).")]
    public string Model { get; set; } = "";

    public string? ApiKey { get; set; }

    [Range(1, int.MaxValue)]
    public int TimeoutSeconds { get; set; } = 300;

    public double Temperature { get; set; } = 0.2;

    [Range(1, int.MaxValue)]
    public int MaxOutputTokens { get; set; } = 4096;

    [Range(1, int.MaxValue)]
    public int MaxPromptTokens { get; set; } = 8000;

    public string SystemPrompt { get; set; } =
        "Jesteś ekspertem od testów jednostkowych w C#. Zwracasz wyłącznie kod C# w jednym bloku.";

    public Dictionary<string, string> Headers { get; set; } = new();
}
