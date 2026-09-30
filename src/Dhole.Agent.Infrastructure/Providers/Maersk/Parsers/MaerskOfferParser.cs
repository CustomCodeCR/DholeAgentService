using System.Globalization;
using System.Text.Json;
using Dhole.Agent.Infrastructure.Providers.Maersk.Models;

namespace Dhole.Agent.Infrastructure.Providers.Maersk.Parsers;

public sealed class MaerskOfferParser
{
    public NormalizedOceanFreightRates Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var candidates = new List<JsonElement>();
        CollectOfferObjects(document.RootElement, candidates);

        var offers = candidates
            .Where(IsOfferedAndAvailable)
            .Select(ParseOffer)
            .Where(x => x is not null)
            .Cast<NormalizedOceanOffer>()
            .GroupBy(x => x.ExternalRouteId, StringComparer.OrdinalIgnoreCase)
            .Select(g => MergeSameRoute(g.ToArray()))
            .ToArray();

        return new NormalizedOceanFreightRates("MAERSK", "MAERSK", offers);
    }

    private static void CollectOfferObjects(JsonElement element, List<JsonElement> output)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(element, "status", out _)
                && (TryGetProperty(element, "routeId", out _)
                    || TryGetProperty(element, "externalRouteId", out _)
                    || TryGetProperty(element, "productDataCollection", out _)))
                output.Add(element);

            foreach (var property in element.EnumerateObject())
                CollectOfferObjects(property.Value, output);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectOfferObjects(item, output);
        }
    }

    private static bool IsOfferedAndAvailable(JsonElement offer)
    {
        var status = GetString(offer, "status");

        if (!string.Equals(status, "OFFERED", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, "AVAILABLE", StringComparison.OrdinalIgnoreCase))
            return false;

        return !TryGetProperty(offer, "availabilityFlag", out var available)
               || available.ValueKind != JsonValueKind.False;
    }

    private static NormalizedOceanOffer? ParseOffer(JsonElement offer)
    {
        var routeId = GetFirstString(
                offer,
                "routeId",
                "externalRouteId",
                "routeIdentifier",
                "id")
            ?? Guid.NewGuid().ToString("N");

        var products = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var charges = new List<NormalizedCharge>();
        var legs = new List<NormalizedLeg>();
        NormalizedMoney? ocean = null;
        NormalizedMoney? allIn = null;

        Visit(offer, node =>
        {
            var productName = GetFirstString(
                node,
                "productName",
                "productCode",
                "productDisplayName");

            if (!string.IsNullOrWhiteSpace(productName))
                products.Add(productName);

            var currency = GetFirstString(
                node,
                "currencyCode",
                "currency",
                "currencyIsoCode",
                "currencyISOCode");

            var basicFreightAmount = GetFirstDecimal(
                node,
                "totalBasicFreightAmount",
                "basicFreightAmount",
                "oceanFreightAmount",
                "freightAmount");

            var chargeType = GetFirstString(
                node,
                "chargeTypeCode",
                "chargeCode",
                "chargeType");

            var application = GetFirstString(
                node,
                "chargeApplicationCode",
                "applicationCode",
                "chargeApplication");

            if (basicFreightAmount.HasValue && currency is not null)
            {
                ocean ??= new NormalizedMoney(
                    currency,
                    basicFreightAmount.Value);
            }
            else if (string.Equals(
                         chargeType,
                         "BAS",
                         StringComparison.OrdinalIgnoreCase)
                     && (string.IsNullOrWhiteSpace(application)
                         || application.Contains(
                             "freight",
                             StringComparison.OrdinalIgnoreCase)))
            {
                var amount = GetFirstDecimal(
                    node,
                    "amount",
                    "totalAmount",
                    "priceAmount");

                if (amount.HasValue && currency is not null)
                    ocean ??= new NormalizedMoney(currency, amount.Value);
            }

            var total = GetFirstDecimal(
                node,
                "allInAmount",
                "totalPrice",
                "totalAmount",
                "totalPriceAmount");

            if (total.HasValue && currency is not null)
                allIn ??= new NormalizedMoney(currency, total.Value);

            var chargeName = GetFirstString(
                node,
                "chargeName",
                "chargeDescription",
                "description");

            if (!string.IsNullOrWhiteSpace(chargeType)
                || !string.IsNullOrWhiteSpace(chargeName))
            {
                var chargeAmount = GetFirstDecimal(
                    node,
                    "amount",
                    "chargeAmount",
                    "totalAmount",
                    "priceAmount",
                    "totalBasicFreightAmount");

                if (chargeAmount.HasValue && currency is not null)
                {
                    charges.Add(new NormalizedCharge(
                        chargeType ?? "UNKNOWN",
                        chargeName,
                        currency,
                        chargeAmount.Value));
                }
            }

            var dep = GetFirstDate(
                node,
                "departureDateTime",
                "departureDate",
                "estimatedDepartureDateTime",
                "estimatedDepartureDate",
                "etd");

            var arr = GetFirstDate(
                node,
                "arrivalDateTime",
                "arrivalDate",
                "estimatedArrivalDateTime",
                "estimatedArrivalDate",
                "eta");

            var isRouteNode =
                string.Equals(
                    GetString(node, "dataBundleType"),
                    "ROUTE_SCHEDULE",
                    StringComparison.OrdinalIgnoreCase)
                || TryGetProperty(node, "routeSchedule", out _)
                || dep.HasValue
                || arr.HasValue;

            if (isRouteNode)
            {
                var from = GetFirstString(
                    node,
                    "from",
                    "origin",
                    "originName",
                    "departureLocationName");

                var to = GetFirstString(
                    node,
                    "to",
                    "destination",
                    "destinationName",
                    "arrivalLocationName");

                var vessel = GetFirstString(
                    node,
                    "vesselName",
                    "vessel",
                    "transportName");

                var voyage = GetFirstString(
                    node,
                    "voyageNumber",
                    "voyage",
                    "transportVoyage");

                if (from is not null
                    || to is not null
                    || dep.HasValue
                    || arr.HasValue
                    || vessel is not null
                    || voyage is not null)
                {
                    legs.Add(new NormalizedLeg(
                        from,
                        to,
                        dep,
                        arr,
                        vessel,
                        voyage));
                }
            }
        });

        var etd = GetFirstDate(
                offer,
                "etd",
                "departureDateTime",
                "departureDate",
                "estimatedDepartureDateTime",
                "estimatedDepartureDate")
            ?? legs
                .Where(x => x.Departure.HasValue)
                .Select(x => x.Departure)
                .Min();

        var eta = GetFirstDate(
                offer,
                "eta",
                "arrivalDateTime",
                "arrivalDate",
                "estimatedArrivalDateTime",
                "estimatedArrivalDate")
            ?? legs
                .Where(x => x.Arrival.HasValue)
                .Select(x => x.Arrival)
                .Max();

        var transit = GetFirstInt(
                offer,
                "transitTime",
                "transitDays",
                "transitTimeInDays",
                "totalTransitDays")
            ?? (etd.HasValue && eta.HasValue
                ? Math.Max(
                    0,
                    (int)Math.Ceiling((eta.Value - etd.Value).TotalDays))
                : 0);

        var vesselName = GetFirstString(
                offer,
                "vesselName",
                "vessel",
                "transportName")
            ?? legs
                .Select(x => x.Vessel)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

        var voyageNumber = GetFirstString(
                offer,
                "voyageNumber",
                "voyage",
                "transportVoyage")
            ?? legs
                .Select(x => x.Voyage)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

        return new NormalizedOceanOffer(
            routeId,
            true,
            etd,
            eta,
            transit,
            vesselName,
            voyageNumber,
            ocean,
            allIn,
            charges.Distinct().ToArray(),
            legs.Distinct().ToArray(),
            products.ToArray());
    }

    private static NormalizedOceanOffer MergeSameRoute(
        IReadOnlyCollection<NormalizedOceanOffer> offers)
    {
        var first = offers.First();

        var etd = offers
            .Select(x => x.Etd)
            .FirstOrDefault(x => x.HasValue);

        var eta = offers
            .Select(x => x.Eta)
            .FirstOrDefault(x => x.HasValue);

        return first with
        {
            Etd = etd,
            Eta = eta,
            TransitDays = offers
                .Select(x => x.TransitDays)
                .FirstOrDefault(x => x > 0),
            Vessel = offers
                .Select(x => x.Vessel)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
            Voyage = offers
                .Select(x => x.Voyage)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
            OceanFreight = offers
                .Select(x => x.OceanFreight)
                .FirstOrDefault(x => x is not null),
            AllIn = offers
                .Select(x => x.AllIn)
                .FirstOrDefault(x => x is not null),
            Charges = offers
                .SelectMany(x => x.Charges)
                .Distinct()
                .ToArray(),
            Legs = offers
                .SelectMany(x => x.Legs)
                .Distinct()
                .ToArray(),
            Products = offers
                .SelectMany(x => x.Products)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static void Visit(
        JsonElement element,
        Action<JsonElement> action)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            action(element);

            foreach (var property in element.EnumerateObject())
                Visit(property.Value, action);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                Visit(item, action);
        }
    }

    private static bool TryGetProperty(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        value = default;

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(
                    property.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetString(
        JsonElement element,
        string name,
        out string? value)
    {
        value = GetString(element, name);
        return value is not null;
    }

    private static string? GetString(
        JsonElement element,
        string name)
        => TryGetProperty(element, name, out var value)
            ? value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.ValueKind == JsonValueKind.Null
                    ? null
                    : value.ToString()
            : null;

    private static string? GetFirstString(
        JsonElement element,
        params string[] names)
    {
        foreach (var name in names)
        {
            var value = GetString(element, name);

            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static decimal? GetFirstDecimal(
        JsonElement element,
        params string[] names)
    {
        foreach (var name in names)
        {
            var value = GetDecimal(element, name);

            if (value.HasValue)
                return value;
        }

        return null;
    }

    private static int? GetFirstInt(
        JsonElement element,
        params string[] names)
    {
        foreach (var name in names)
        {
            var value = GetInt(element, name);

            if (value.HasValue)
                return value;
        }

        return null;
    }

    private static DateTimeOffset? GetFirstDate(
        JsonElement element,
        params string[] names)
    {
        foreach (var name in names)
        {
            var value = GetDate(element, name);

            if (value.HasValue)
                return value;
        }

        return null;
    }

    private static decimal? GetDecimal(
        JsonElement element,
        string name)
    {
        if (!TryGetProperty(element, name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetDecimal(out var number))
            return number;

        return decimal.TryParse(
            value.ToString(),
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out number)
            ? number
            : null;
    }

    private static int? GetInt(
        JsonElement element,
        string name)
    {
        if (!TryGetProperty(element, name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number))
            return number;

        return int.TryParse(value.ToString(), out number)
            ? number
            : null;
    }

    private static DateTimeOffset? GetDate(
        JsonElement element,
        string name)
        => TryGetProperty(element, name, out var value)
           && DateTimeOffset.TryParse(
               value.ToString(),
               CultureInfo.InvariantCulture,
               DateTimeStyles.AssumeUniversal,
               out var date)
            ? date
            : null;
}
