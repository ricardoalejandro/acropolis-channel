using System.Text.RegularExpressions;

namespace Acropolis.Catalog.Application;

public static class TopicRules
{
    public const int MaximumTopicsPerContent = 12;
    public static bool ValidVersion(string? value) => value is not null && Regex.IsMatch(value, "^[a-f0-9]{32}\\z", RegexOptions.CultureInvariant);
    public static bool ValidName(string? value) => value is not null && value.Length <= 180 && value.Trim().Length >= 2 && !value.Any(char.IsControl);
    public static bool ValidStatus(string? value) => value is "active" or "archived";
    public static Dictionary<string, string[]> Validate(CreateTopicRequest request)
    {
        var errors = NameErrors(request.Name);
        if (!CatalogRules.ValidSlug(request.Slug)) errors["slug"] = ["Usa una dirección de entre 2 y 160 caracteres, en minúsculas y con guiones simples."];
        return errors;
    }
    public static Dictionary<string, string[]> Validate(UpdateTopicRequest request)
    {
        var errors = NameErrors(request.Name);
        if (!ValidVersion(request.Version)) errors["version"] = ["La versión es obligatoria."];
        return errors;
    }
    public static Dictionary<string, string[]> Validate(TopicStateRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (!ValidVersion(request.Version)) errors["version"] = ["La versión es obligatoria."];
        if (!ValidStatus(request.Status)) errors["status"] = ["Selecciona activo o archivado."];
        return errors;
    }
    public static Dictionary<string, string[]> Validate(MoveTopicRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (!ValidVersion(request.DirectoryVersion)) errors["directoryVersion"] = ["Recarga el directorio antes de ordenar."];
        if (request.Id == Guid.Empty || request.BeforeId == Guid.Empty || request.BeforeId == request.Id) errors["id"] = ["Selecciona dos temas diferentes o mueve el tema al final."];
        return errors;
    }
    public static Dictionary<string, string[]> Validate(AssignContentTopicsRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (!ValidVersion(request.ContentVersion)) errors["contentVersion"] = ["La versión del contenido es obligatoria."];
        if (request.TopicIds is null || request.TopicIds.Length > MaximumTopicsPerContent || request.TopicIds.Any(x => x == Guid.Empty) || request.TopicIds.Distinct().Count() != request.TopicIds.Length)
            errors["topicIds"] = ["Selecciona hasta 12 temas distintos."];
        return errors;
    }
    public static Dictionary<string, string[]> ValidateQuery(string? search, string? status, int page, int pageSize)
    {
        var errors = CatalogRules.ValidateQuery(search, null, null, page, pageSize);
        if (status is not null && !ValidStatus(status)) errors["status"] = ["Estado de tema no válido."];
        return errors;
    }
    private static Dictionary<string, string[]> NameErrors(string? name) => ValidName(name) ? new() : new() { ["name"] = ["El nombre debe tener entre 2 y 180 caracteres sin controles."] };
}
