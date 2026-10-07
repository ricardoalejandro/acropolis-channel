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
    public static IReadOnlyList<string> AuditActions { get; } = Array.AsReadOnly(new[] { "content.created", "content.updated", "content.published", "content.withdrawn", "content.archived", "content.restored", "content.topics_updated" });
    public static Dictionary<string, string[]> ValidateAuditQuery(DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? action, Guid? contentId, int page, int pageSize)
    {
        var errors = ValidateQuery(null, null, null, page, pageSize);
        if (fromUtc is not null && fromUtc.Value.Offset != TimeSpan.Zero) errors["fromUtc"] = ["Usa una fecha UTC."];
        if (toUtc is not null && toUtc.Value.Offset != TimeSpan.Zero) errors["toUtc"] = ["Usa una fecha UTC."];
        if (fromUtc is not null && toUtc is not null && fromUtc > toUtc) errors["toUtc"] = ["El final debe ser posterior o igual al inicio."];
        if (action is not null && !AuditActions.Contains(action)) errors["action"] = ["Acción no válida."];
        if (contentId == Guid.Empty) errors["contentId"] = ["Objeto no válido."];
        return errors;
    }
    public static bool ValidSlug(string? value) => value is not null && value.Length is >= 2 and <= 160 && Regex.IsMatch(value, "^[a-z0-9]+(-[a-z0-9]+)*\\z", RegexOptions.CultureInvariant);
    public static bool ValidYouTubeId(string? value) => value is not null && Regex.IsMatch(value, "^[A-Za-z0-9_-]{11}\\z", RegexOptions.CultureInvariant);
    public static bool ValidCategory(string? value) => Categories.Any(x => x.Id == value);
    public static bool VideoCategory(string? value) => value is "documentales" or "videos" or "podcast" or "charlas-online";
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
        if (request.Author is not null && !SingleLine(request.Author, 1, 180)) errors["author"] = ["El autor admite entre 1 y 180 caracteres sin controles."];
        var tags = request.Tags ?? [];
        if (tags.Length > 12 || tags.Any(x => !SingleLine(x, 1, 40)) || tags.Select(x => x?.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != tags.Length)
            errors["tags"] = ["Incluye hasta 12 etiquetas distintas de entre 1 y 40 caracteres."];
        if (request.WorkText is not null && (!PlainText(request.WorkText, 1, 500000) || request.Category != "lecturas" || request.CollectionKind is not null))
            errors["workText"] = ["La obra debe ser texto plano de hasta 500.000 caracteres, sólo para lecturas."];
        if (request.YouTubeId is not null && (!ValidYouTubeId(request.YouTubeId) || !VideoCategory(request.Category) || request.CollectionKind is not null))
            errors["youTubeId"] = ["Incluye sólo el identificador YouTube de 11 caracteres para contenido audiovisual."];
        if (request.CollectionKind is not null && (request.CollectionKind is not ("course" or "program") || request.Category != "cursos"))
            errors["collectionKind"] = ["Selecciona curso o programa dentro de la categoría Cursos."];
        var items = request.ItemIds ?? [];
        if (items.Length > 100 || items.Any(x => x == Guid.Empty) || items.Distinct().Count() != items.Length || (items.Length > 0 && request.CollectionKind is null) || (status == "published" && request.CollectionKind is not null && items.Length == 0))
            errors["itemIds"] = ["Incluye hasta 100 referencias distintas y ordenadas; una colección publicada requiere elementos."];
        return errors;
    }
    public static Dictionary<string, string[]> Validate(UpdateContentRequest request)
    {
        var errors = Validate(new(request.Slug, request.Title, request.Summary, request.Body, request.Category, request.CoverAsset, request.DurationSeconds, request.Author, request.Tags, request.WorkText, request.YouTubeId, request.CollectionKind, request.ItemIds), request.Status);
        if (request.Version is null || !Regex.IsMatch(request.Version, "^[a-f0-9]{32}\\z", RegexOptions.CultureInvariant)) errors["version"] = ["La versión es obligatoria."];
        if (!ValidStatus(request.Status)) errors["status"] = ["Estado no válido."];
        return errors;
    }
    private static bool SingleLine(string? value, int minimum, int maximum) => value is not null && value.Trim().Length >= minimum && value.Length <= maximum && !value.Any(char.IsControl);
    private static bool PlainText(string? value, int minimum, int maximum) => value is not null && value.Trim().Length >= minimum && value.Length <= maximum && !value.Any(x => char.IsControl(x) && x is not ('\n' or '\r' or '\t'));
}
