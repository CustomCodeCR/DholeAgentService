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
            if (TryGetString(element, "status", out _) &&
                (TryGetProperty(element, "routeId", out _) || TryGetProperty(element, "productDataCollection", out _)))
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
        if (!string.Equals(status, "OFFERED", StringComparison.OrdinalIgnoreCase))
            return false;

        return !TryGetProperty(offer, "availabilityFlag", out var available) ||
               available.ValueKind != JsonValueKind.False;
    }

    private static NormalizedOceanOffer? ParseOffer(JsonElement offer)
    {
        var routeId = GetString(offer, "routeId")
            ?? GetString(offer, "externalRouteId")
            ?? Guid.NewGuid().ToString("N");

        var products = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var charges = new List<NormalizedCharge>();
        var legs = new List<NormalizedLeg>();
        NormalizedMoney? ocean = null;
        NormalizedMoney? allIn = null;

        Visit(offer, node =>
        {
            var productName = GetString(node, "productName") ?? GetString(node, "productCode");
            if (!string.IsNullOrWhiteSpace(productName))
                products.Add(productName);

            var chargeType = GetString(node, "chargeTypeCode");
            var application = GetString(node, "chargeApplicationCode");
            var currency = GetString(node, "currencyCode") ?? GetString(node, "currency");

            if (string.Equals(chargeType, "BAS", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(application, "Freight", StringComparison.OrdinalIgnoreCase))
            {
                var amount = GetDecimal(node, "totalBasicFreightAmount")
                    ?? GetDecimal(node, "amount");
                if (amount.HasValue && currency is not null)
                    ocean = new NormalizedMoney(currency, amount.Value);
            }

            var total = GetDecimal(node, "totalAmount");
            if (total.HasValue && currency is not null)
                allIn ??= new NormalizedMoney(currency, total.Value);

            if (!string.IsNullOrWhiteSpace(chargeType))
            {
                var chargeAmount = GetDecimal(node, "amount") ?? GetDecimal(node, "totalAmount");
                if (chargeAmount.HasValue && currency is not null)
                    charges.Add(new NormalizedCharge(chargeType, GetString(node, "chargeName"), currency, chargeAmount.Value));
            }

            if (string.Equals(GetString(node, "dataBundleType"), "ROUTE_SCHEDULE", StringComparison.OrdinalIgnoreCase) ||
                TryGetProperty(node, "routeSchedule", out _))
            {
                var from = GetString(node, "from") ?? GetString(node, "origin");
                var to = GetString(node, "to") ?? GetString(node, "destination");
                var dep = GetDate(node, "departureDateTime") ?? GetDate(node, "etd");
                var arr = GetDate(node, "arrivalDateTime") ?? GetDate(node, "eta");
                if (from is not null || to is not null || dep.HasValue || arr.HasValue)
                    legs.Add(new NormalizedLeg(from,to,dep,arr,GetString(node,"vesselName")??GetString(node,"vessel"),GetString(node,"voyageNumber")??GetString(node,"voyage")));
            }
        });

        var etd = GetDate(offer, "etd") ?? legs.Where(x => x.Departure.HasValue).Select(x => x.Departure).Min();
        var eta = GetDate(offer, "eta") ?? legs.Where(x => x.Arrival.HasValue).Select(x => x.Arrival).Max();
        var transit = GetInt(offer, "transitTime") ?? (etd.HasValue && eta.HasValue ? Math.Max(0,(int)Math.Ceiling((eta.Value-etd.Value).TotalDays)) : 0);
        var vessel = GetString(offer,"vesselName") ?? legs.Select(x=>x.Vessel).FirstOrDefault(x=>!string.IsNullOrWhiteSpace(x));
        var voyage = GetString(offer,"voyageNumber") ?? legs.Select(x=>x.Voyage).FirstOrDefault(x=>!string.IsNullOrWhiteSpace(x));

        return new NormalizedOceanOffer(routeId,true,etd,eta,transit,vessel,voyage,ocean,allIn,charges,legs,products.ToArray());
    }

    private static NormalizedOceanOffer MergeSameRoute(IReadOnlyCollection<NormalizedOceanOffer> offers)
    {
        var first=offers.First();
        return first with
        {
            OceanFreight=offers.Select(x=>x.OceanFreight).FirstOrDefault(x=>x is not null),
            AllIn=offers.Select(x=>x.AllIn).FirstOrDefault(x=>x is not null),
            Charges=offers.SelectMany(x=>x.Charges).Distinct().ToArray(),
            Legs=offers.SelectMany(x=>x.Legs).Distinct().ToArray(),
            Products=offers.SelectMany(x=>x.Products)
                .Where(x=>!string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static void Visit(JsonElement element, Action<JsonElement> action)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            action(element);
            foreach(var property in element.EnumerateObject()) Visit(property.Value,action);
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach(var item in element.EnumerateArray()) Visit(item,action);
    }

    private static bool TryGetProperty(JsonElement element,string name,out JsonElement value)
    {
        value=default;
        if(element.ValueKind!=JsonValueKind.Object) return false;
        foreach(var p in element.EnumerateObject())
            if(string.Equals(p.Name,name,StringComparison.OrdinalIgnoreCase)){value=p.Value;return true;}
        return false;
    }
    private static bool TryGetString(JsonElement element,string name,out string? value){value=GetString(element,name);return value is not null;}
    private static string? GetString(JsonElement element,string name)
        => TryGetProperty(element,name,out var v) ? v.ValueKind==JsonValueKind.String?v.GetString():v.ToString() : null;
    private static decimal? GetDecimal(JsonElement element,string name)
    {
        if(!TryGetProperty(element,name,out var v))return null;
        if(v.ValueKind==JsonValueKind.Number&&v.TryGetDecimal(out var d))return d;
        return decimal.TryParse(v.ToString(),NumberStyles.Any,CultureInfo.InvariantCulture,out d)?d:null;
    }
    private static int? GetInt(JsonElement element,string name)
    {
        if(!TryGetProperty(element,name,out var v))return null;
        if(v.ValueKind==JsonValueKind.Number&&v.TryGetInt32(out var i))return i;
        return int.TryParse(v.ToString(),out i)?i:null;
    }
    private static DateTimeOffset? GetDate(JsonElement element,string name)
        => TryGetProperty(element,name,out var v)&&DateTimeOffset.TryParse(v.ToString(),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var d)?d:null;
}
