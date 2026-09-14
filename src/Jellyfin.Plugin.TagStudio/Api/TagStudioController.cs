using Jellyfin.Plugin.TagStudio.Models;
using Jellyfin.Plugin.TagStudio.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TagStudio.Api;

[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("TagStudio")]
[Produces("application/json")]
public class TagStudioController : ControllerBase
{
    private readonly LibraryQueryService _query;
    private readonly MetadataWriter _writer;
    private readonly OperationJournal _journal;

    public TagStudioController(
        LibraryQueryService query,
        MetadataWriter writer,
        OperationJournal journal)
    {
        _query = query;
        _writer = writer;
        _journal = journal;
    }

    /// <summary>All browser columns - tags, genres, years, studios - with counts.</summary>
    [HttpGet("Facets")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<FacetsResponse> GetFacets([FromQuery] string? itemTypes)
    {
        return Ok(_query.GetFacets(SplitCsv(itemTypes)));
    }

    /// <summary>Characters this server would split a tag on.</summary>
    [HttpGet("ReservedCharacters")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<string>> GetReservedCharacters()
    {
        return Ok(_writer.ReservedCharacters().Select(c => c.ToString()));
    }

    [HttpPost("Query")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ItemQueryResponse> Query([FromBody] ItemQueryRequest request)
    {
        return Ok(_query.Query(request));
    }

    [HttpPost("Apply")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<OperationResult>> Apply(
        [FromBody] ApplyRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _writer.ApplyAsync(request, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Renames a tag or genre across the whole library. Renaming onto a value that
    /// already exists merges the two.
    /// </summary>
    [HttpPost("Rename")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<OperationResult>> Rename(
        [FromBody] RenameRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.From) || string.IsNullOrWhiteSpace(request.To))
        {
            return BadRequest("Both From and To are required.");
        }

        var affected = _query.FindItemsWith(request.Field, request.From);
        var result = await _writer.ApplyAsync(
            new ApplyRequest
            {
                ItemIds = affected,
                Field = request.Field,
                Add = new[] { request.To },
                Remove = new[] { request.From }
            },
            cancellationToken).ConfigureAwait(false);

        return Ok(result);
    }

    /// <summary>Removes a tag or genre from every item that carries it.</summary>
    [HttpPost("Delete")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<OperationResult>> Delete(
        [FromQuery] string value,
        [FromQuery] FieldKind field,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return BadRequest("A value is required.");
        }

        if (field == FieldKind.Tag && LibraryQueryService.IsMachineName(value))
        {
            return BadRequest(
                $"\"{value}\" is a machine-generated tag and would be recreated on the next refresh. "
                + "Disable the plugin that generates it instead.");
        }

        var affected = _query.FindItemsWith(field, value);
        var result = await _writer.ApplyAsync(
            new ApplyRequest
            {
                ItemIds = affected,
                Field = field,
                Remove = new[] { value }
            },
            cancellationToken).ConfigureAwait(false);

        return Ok(result);
    }

    [HttpGet("History")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<Operation>> History([FromQuery] int take = 25)
    {
        return Ok(_journal.ListRecent(Math.Clamp(take, 1, 200)));
    }

    [HttpPost("Undo/{operationId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OperationResult>> Undo(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var result = await _writer.UndoAsync(operationId, cancellationToken).ConfigureAwait(false);
        return result is null ? NotFound() : Ok(result);
    }

    private static string[] SplitCsv(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
