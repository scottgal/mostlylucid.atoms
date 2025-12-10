using RazorLight;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.Ephemeral.Atoms.RazorTemplate;

/// <summary>
///     Razor template rendering atom for generating HTML emails and content.
///
///     Uses RazorLight for in-memory Razor compilation and rendering.
/// </summary>
public class RazorTemplateAtom : IAsyncDisposable
{
    private readonly RazorLightEngine _engine;
    private readonly SignalSink? _signals;

    public RazorTemplateAtom(RazorTemplateOptions? options = null, SignalSink? signals = null)
    {
        options ??= new RazorTemplateOptions();
        _signals = signals;

        var builder = new RazorLightEngineBuilder();

        if (!string.IsNullOrEmpty(options.TemplateRootPath))
        {
            builder.UseFileSystemProject(options.TemplateRootPath);
        }
        else
        {
            builder.UseEmbeddedResourcesProject(typeof(RazorTemplateAtom));
        }

        if (options.EnableCaching)
        {
            builder.UseMemoryCachingProvider();
        }

        _engine = builder.Build();
    }

    /// <summary>
    ///     Render a template from a string with a model.
    /// </summary>
    public async Task<RenderResult> RenderStringAsync<TModel>(
        string templateKey,
        string templateContent,
        TModel model,
        CancellationToken cancellationToken = default)
    {
        _signals?.Raise($"razor.render.started:key={templateKey}");

        try
        {
            var result = await _engine.CompileRenderStringAsync(templateKey, templateContent, model);

            _signals?.Raise($"razor.render.success:key={templateKey}:length={result.Length}");

            return new RenderResult
            {
                Success = true,
                Content = result,
                Description = "Template rendered successfully"
            };
        }
        catch (Exception ex)
        {
            _signals?.Raise($"razor.render.failed:key={templateKey}:error={ex.Message}");

            return new RenderResult
            {
                Success = false,
                Content = string.Empty,
                Description = $"Failed to render template: {ex.Message}"
            };
        }
    }

    /// <summary>
    ///     Render a template from a file with a model.
    /// </summary>
    public async Task<RenderResult> RenderFileAsync<TModel>(
        string templatePath,
        TModel model,
        CancellationToken cancellationToken = default)
    {
        _signals?.Raise($"razor.render.file.started:path={templatePath}");

        try
        {
            var result = await _engine.CompileRenderAsync(templatePath, model);

            _signals?.Raise($"razor.render.file.success:path={templatePath}:length={result.Length}");

            return new RenderResult
            {
                Success = true,
                Content = result,
                Description = "Template rendered successfully"
            };
        }
        catch (Exception ex)
        {
            _signals?.Raise($"razor.render.file.failed:path={templatePath}:error={ex.Message}");

            return new RenderResult
            {
                Success = false,
                Content = string.Empty,
                Description = $"Failed to render template: {ex.Message}"
            };
        }
    }

    /// <summary>
    ///     Render an email template with common email model.
    /// </summary>
    public async Task<RenderResult> RenderEmailAsync(
        string templateKey,
        string templateContent,
        EmailModel model,
        CancellationToken cancellationToken = default)
    {
        return await RenderStringAsync(templateKey, templateContent, model, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        // RazorLightEngine doesn't need explicit disposal
        await Task.CompletedTask;
    }
}

/// <summary>
///     Razor template configuration options.
/// </summary>
public class RazorTemplateOptions
{
    /// <summary>Root path for file-based templates. If null, uses embedded resources.</summary>
    public string? TemplateRootPath { get; init; }

    /// <summary>Enable caching of compiled templates. Default: true</summary>
    public bool EnableCaching { get; init; } = true;
}

/// <summary>
///     Common email template model.
/// </summary>
public class EmailModel
{
    public string? RecipientName { get; init; }
    public string? RecipientEmail { get; init; }
    public string? Subject { get; init; }
    public string? PreheaderText { get; init; }
    public Dictionary<string, object>? CustomData { get; init; }
}

/// <summary>
///     Result from template rendering.
/// </summary>
public record RenderResult
{
    public bool Success { get; init; }
    public string Content { get; init; }
    public string Description { get; init; } = string.Empty;
}
