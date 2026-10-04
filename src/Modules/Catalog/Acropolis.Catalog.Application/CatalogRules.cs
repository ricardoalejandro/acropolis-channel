using System.Text.RegularExpressions;

namespace Acropolis.Catalog.Application;

public static class CatalogRules
{
    public static IReadOnlyList<CategoryView> Categories { get; } = Array.AsReadOnly(new[]
    {
        new CategoryView("lecturas", "Lecturas"), new CategoryView("documentales", "Documentales"),
        new CategoryView("videos", "Videos"), new CategoryView("podcast", "Podcast"),
        new CategoryView("charlas-online", "Charlas online"), new CategoryView("cursos", "Cursos")
    });
    public static IReadOnlyList<string> CoverAssets { get; } = Array.AsReadOnly(new[] { "hero-acropolis", "editorial-reading", "editorial-podcast", "editorial-dialogue", "editorial-nature" });
    public static bool ValidSlug(string? value) => value is not null && value.Length is >= 2 and <= 160 && Regex.IsMatch(value, "^[a-z0-9]+(-[a-z0-9]+)*\\z", RegexOptions.CultureInvariant);
    public static bool ValidCategory(string? value) => Categories.Any(x => x.Id == value);
    public static bool ValidStatus(string? value) => value is "draft" or "published" or "archived";
    public static bool CanTransition(string from, string to) => ValidStatus(from) && ValidStatus(to) &&
        (from == to || (from, to) is ("draft", "published") or ("published", "draft") or ("published", "archived") or ("archived", "draft"));
    public static string EscapeSearch(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    public static Dictionary<string, string[]> ValidateQuery(string? search, string? category, string? status, int page, int pageSize)
    {
        var errors = new Dictionary<string, string[]>();
        if (search is not null && (search.Length > 100 || search.Any(char.IsControl))) errors["search"] = ["La búsqueda admite hasta 100 caracteres sin controles."];
        if (category is not null && !ValidCategory(category)) errors["category"] = ["Categoría no válida."];
        if (status is not null && !ValidStatus(status)) errors["status"] = ["Estado no válido."];
        if (page is < 1 or > 1000000) errors["page"] = ["Página no válida."];
        if (pageSize is < 1 or > 100) errors["pageSize"] = ["El tamaño de página debe estar entre 1 y 100."];
        return errors;
    }
    public static Dictionary<string, string[]> Validate(CreateContentRequest request, string status = "draft")
    {
        var errors = new Dictionary<string, string[]>();
        if (!ValidSlug(request.Slug)) errors["slug"] = ["Usa entre 2 y 160 caracteres: letras minúsculas, números y guiones simples."];
        if (!SingleLine(request.Title, 2, 180)) errors["title"] = ["El título debe tener entre 2 y 180 caracteres sin controles."];
        if (!SingleLine(request.Summary, status == "published" ? 1 : 0, 600)) errors["summary"] = ["Incluye un resumen de hasta 600 caracteres; es obligatorio al publicar."];
        if (!PlainText(request.Body, status == "published" ? 1 : 0, 50000)) errors["body"] = ["Incluye una sinopsis editorial pública de hasta 50.000 caracteres; es obligatoria al publicar."];
        if (!ValidCategory(request.Category)) errors["category"] = ["Categoría no válida."];
        if (request.CoverAsset is not null && !CoverAssets.Contains(request.CoverAsset)) errors["coverAsset"] = ["Selecciona una cubierta aprobada."];
        if (request.DurationSeconds is not null && request.DurationSeconds is < 1 or > 86400) errors["durationSeconds"] = ["La duración debe estar entre 1 y 86.400 segundos."];
        return errors;
    }
    public static Dictionary<string, string[]> Validate(UpdateContentRequest request)
    {
        var errors = Validate(new(request.Slug, request.Title, request.Summary, request.Body, request.Category, request.CoverAsset, request.DurationSeconds), request.Status);
        if (request.Version is null || !Regex.IsMatch(request.Version, "^[a-f0-9]{32}\\z", RegexOptions.CultureInvariant)) errors["version"] = ["La versión es obligatoria."];
        if (!ValidStatus(request.Status)) errors["status"] = ["Estado no válido."];
        return errors;
    }
    private static bool SingleLine(string? value, int minimum, int maximum) => value is not null && value.Trim().Length >= minimum && value.Length <= maximum && !value.Any(char.IsControl);
    private static bool PlainText(string? value, int minimum, int maximum) => value is not null && value.Trim().Length >= minimum && value.Length <= maximum && !value.Any(x => char.IsControl(x) && x is not ('\n' or '\r' or '\t'));
}
