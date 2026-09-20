using System.Text.Json.Serialization;

namespace Dhole.Agent.Infrastructure.Providers.Maersk.Models;

public sealed record MaerskSearchInput(
    string Pol,
    string Pod,
    string ContainerType,
    int Quantity,
    decimal WeightKg,
    string Commodity,
    DateOnly CargoReadyDate);

public sealed record NormalizedMoney(string Currency, decimal Amount);
public sealed record NormalizedCharge(string Code, string? Name, string Currency, decimal Amount);
public sealed record NormalizedLeg(string? From, string? To, DateTimeOffset? Departure, DateTimeOffset? Arrival, string? Vessel, string? Voyage);

public sealed record NormalizedOceanOffer(
    string ExternalRouteId,
    bool Available,
    DateTimeOffset? Etd,
    DateTimeOffset? Eta,
    int TransitDays,
    string? Vessel,
    string? Voyage,
    NormalizedMoney? OceanFreight,
    NormalizedMoney? AllIn,
    IReadOnlyCollection<NormalizedCharge> Charges,
    IReadOnlyCollection<NormalizedLeg> Legs,
    IReadOnlyCollection<string> Products);

public sealed record NormalizedOceanFreightRates(
    string Provider,
    string Carrier,
    IReadOnlyCollection<NormalizedOceanOffer> Offers);

public sealed record CapturedMaerskOfferResponse(
    string? RequestBody,
    int Status,
    string? CorrelationId,
    string ResponseJson);
