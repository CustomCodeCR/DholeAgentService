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

    private static void CollectOfferObjects(
        JsonElement element,
        List<JsonElement> output)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(element, "status", out _)
                && (TryGetProperty(element, "routeId", out _)
                    || TryGetProperty(element, "productDataCollection", out _)))
            {
                output.Add(element);
            }

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

        if (!string.Equals(
                status,
                "OFFERED",
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                status,
                "AVAILABLE",
                StringComparison.OrdinalIgnoreCase))
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

        NormalizedMoney? oceanFreight = null;
        NormalizedMoney? allIn = null;
        DateTimeOffset? cargoCutoff = null;

        Visit(offer, node =>
        {
            var productName = GetFirstString(
                node,
                "productName",
                "productCode",
                "productDisplayName");

            if (!string.IsNullOrWhiteSpace(productName))
                products.Add(productName);

            // Current Maersk PRICE_BREAKDOWN returns money as:
            // { "unit": "USD", "value": 8150.0 }
            oceanFreight ??= GetFirstMoney(
                node,
                "totalBasicFreightAmount",
                "basicFreightAmount",
                "oceanFreightAmount",
                "freightAmount");

            allIn ??= GetFirstMoney(
                node,
                "totalAmount",
                "allInAmount",
                "totalPrice",
                "totalPriceAmount");

            var chargeType = GetFirstString(
                node,
                "chargeTypeCode",
                "chargeCode",
                "chargeType");

            var chargeName = GetFirstString(
                node,
                "chargeTypeName",
                "chargeName",
                "chargeDescription",
                "displayName",
                "description");

            if (!string.IsNullOrWhiteSpace(chargeType)
                || !string.IsNullOrWhiteSpace(chargeName))
            {
                var chargeMoney = GetFirstMoney(
                    node,
                    "amount",
                    "amountInOriginalCurrency",
                    "unitPrice",
                    "unitPriceInOriginalCurrency");

                if (chargeMoney is not null)
                {
                    charges.Add(new NormalizedCharge(
                        chargeType ?? "UNKNOWN",
                        chargeName,
                        chargeMoney.Currency,
                        chargeMoney.Amount,
                        GetFirstString(
                            node,
                            "chargeApplicationCode",
                            "applicationCode",
                            "chargeApplication")));

                    if (oceanFreight is null
                        && string.Equals(
                            chargeType,
                            "BAS",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        oceanFreight = chargeMoney;
                    }
                }
            }

            // Commercial Cargo Cutoff (CCC). This is the ETD field requested by
            // the extraction profile, while the sailing departure is preserved
            // separately on the offer/legs.
            if (string.Equals(
                    GetString(node, "code"),
                    "CCC",
                    StringComparison.OrdinalIgnoreCase))
            {
                cargoCutoff ??= GetDate(node, "date");
            }

            // Current ROUTE_SCHEDULE shape:
            // schedules[].originDepartureDatetime
            // schedules[].destinationArrivalDatetime
            var departure = GetFirstDate(
                node,
                "originDepartureDatetime",
                "departureDateTime",
                "departureDate",
                "estimatedDepartureDateTime",
                "estimatedDepartureDate",
                "etd");

            var arrival = GetFirstDate(
                node,
                "destinationArrivalDatetime",
                "arrivalDateTime",
                "arrivalDate",
                "estimatedArrivalDateTime",
                "estimatedArrivalDate",
                "eta");

            if (departure.HasValue || arrival.HasValue)
            {
                var from =
                    GetNestedString(node, "startLocation", "cityName")
                    ?? GetNestedString(node, "startLocation", "siteName")
                    ?? GetNestedString(node, "startLocation", "unLocode")
                    ?? GetFirstString(
                        node,
                        "from",
                        "origin",
                        "originName",
                        "departureLocationName");

                var to =
                    GetNestedString(node, "endLocation", "cityName")
                    ?? GetNestedString(node, "endLocation", "siteName")
                    ?? GetNestedString(node, "endLocation", "unLocode")
                    ?? GetFirstString(
                        node,
                        "to",
                        "destination",
                        "destinationName",
                        "arrivalLocationName");

                var vessel =
                    GetNestedString(node, "sailing", "vessel", "name")
                    ?? GetFirstString(
                        node,
                        "vesselName",
                        "transportName");

                var voyage =
                    GetNestedString(node, "sailing", "voyageNumber")
                    ?? GetFirstString(
                        node,
                        "voyageNumber",
                        "voyage",
                        "transportVoyage");

                legs.Add(new NormalizedLeg(
                    from,
                    to,
                    departure,
                    arrival,
                    vessel,
                    voyage));
            }
        });

        var etd = legs
            .Where(x => x.Departure.HasValue)
            .Select(x => x.Departure)
            .Min();

        var eta = legs
            .Where(x => x.Arrival.HasValue)
            .Select(x => x.Arrival)
            .Max();

        var rawTransit = FindFirstIntRecursive(
            offer,
            "transitTime");

        var transitDays = NormalizeTransitDays(rawTransit);
        if (transitDays == 0 && etd.HasValue && eta.HasValue)
        {
            transitDays = Math.Max(
                0,
                (int)Math.Ceiling((eta.Value - etd.Value).TotalDays));
        }

        var vessel = legs
            .Select(x => x.Vessel)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

        var voyage = legs
            .Select(x => x.Voyage)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

        return new NormalizedOceanOffer(
            routeId,
            true,
            etd,
            eta,
            transitDays,
            vessel,
            voyage,
            oceanFreight,
            allIn,
            charges.Distinct().ToArray(),
            legs.Distinct().ToArray(),
            products.ToArray(),
            cargoCutoff);
    }

    private static NormalizedOceanOffer MergeSameRoute(
        IReadOnlyCollection<NormalizedOceanOffer> offers)
    {
        var first = offers.First();

        return first with
        {
            Etd = offers
                .Select(x => x.Etd)
                .FirstOrDefault(x => x.HasValue),
            Eta = offers
                .Select(x => x.Eta)
                .FirstOrDefault(x => x.HasValue),
            CargoCutoff = offers
                .Select(x => x.CargoCutoff)
                .FirstOrDefault(x => x.HasValue),
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

    private static NormalizedMoney? GetFirstMoney(
        JsonElement element,
        params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(element, propertyName, out var value))
                continue;

            var money = ParseMoney(element, value);

            if (money is not null)
                return money;
        }

        return null;
    }

    private static NormalizedMoney? ParseMoney(
        JsonElement parent,
        JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var currency = GetFirstString(
                value,
                "unit",
                "currency",
                "currencyCode",
                "currencyIsoCode");

            var amount = GetFirstDecimal(
                value,
                "value",
                "amount");

            return !string.IsNullOrWhiteSpace(currency) && amount.HasValue
                ? new NormalizedMoney(currency, amount.Value)
                : null;
        }

        decimal? scalarAmount = null;

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetDecimal(out var number))
        {
            scalarAmount = number;
        }
        else if (decimal.TryParse(
                     value.ToString(),
                     NumberStyles.Any,
                     CultureInfo.InvariantCulture,
                     out number))
        {
            scalarAmount = number;
        }

        if (!scalarAmount.HasValue)
            return null;

        var parentCurrency = GetFirstString(
            parent,
            "currencyCode",
            "currency",
            "currencyIsoCode",
            "currencyISOCode");

        return string.IsNullOrWhiteSpace(parentCurrency)
            ? null
            : new NormalizedMoney(parentCurrency, scalarAmount.Value);
    }

    private static string? GetNestedString(
        JsonElement element,
        params string[] path)
    {
        var current = element;

        foreach (var segment in path)
        {
            if (!TryGetProperty(current, segment, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.String
            ? current.GetString()
            : current.ValueKind == JsonValueKind.Null
                ? null
                : current.ToString();
    }

    private static int? FindFirstIntRecursive(
        JsonElement element,
        string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProperty(element, propertyName, out var value))
            {
                if (value.ValueKind == JsonValueKind.Number
                    && value.TryGetInt32(out var number))
                    return number;

                if (int.TryParse(
                        value.ToString(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out number))
                    return number;
            }

            foreach (var property in element.EnumerateObject())
            {
                var nested = FindFirstIntRecursive(
                    property.Value,
                    propertyName);

                if (nested.HasValue)
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindFirstIntRecursive(
                    item,
                    propertyName);

                if (nested.HasValue)
                    return nested;
            }
        }

        return null;
    }

    private static int NormalizeTransitDays(int? rawTransit)
    {
        if (!rawTransit.HasValue || rawTransit.Value <= 0)
            return 0;

        // Maersk currently returns ROUTE_SCHEDULE transitTime in minutes
        // (e.g. 67440), while older fixtures used a day count directly.
        return rawTransit.Value > 365
            ? (int)Math.Ceiling(rawTransit.Value / 1440d)
            : rawTransit.Value;
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
            if (!TryGetProperty(element, name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number
                && value.TryGetDecimal(out var number))
                return number;

            if (decimal.TryParse(
                    value.ToString(),
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out number))
                return number;
        }

        return null;
    }

    private static DateTimeOffset? GetFirstDate(
        JsonElement element,
        params string[] names)
    {
        foreach (var name in names)
        {
            var date = GetDate(element, name);

            if (date.HasValue)
                return date;
        }

        return null;
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
