using Dhole.Agent.Infrastructure.Providers.Maersk.Parsers;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class MaerskOfferParserTests
{
    private static string LoadFixture()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Maersk",
            "departures-offers-response.json");

        return File.ReadAllText(path);
    }

    [TestMethod]
    public void Parse_ShouldIgnoreNotOffered()
    {
        var result = new MaerskOfferParser().Parse(LoadFixture());

        Assert.IsFalse(result.Offers.Any(x => x.ExternalRouteId == "ROUTE-NOT-OFFERED"));
    }

    [TestMethod]
    public void Parse_ShouldPreserveUnavailableRoutes()
    {
        var result = new MaerskOfferParser().Parse(LoadFixture());

        var unavailable = result.Offers.Single(x => x.ExternalRouteId == "ROUTE-UNAVAILABLE");
        Assert.IsFalse(unavailable.Available);
    }

    [TestMethod]
    public void Parse_ShouldExtractBasicOceanFreight()
    {
        var offer = new MaerskOfferParser().Parse(LoadFixture()).Offers
            .Single(x => x.ExternalRouteId == "ROUTE-001");

        Assert.IsNotNull(offer.OceanFreight);
        Assert.AreEqual("USD", offer.OceanFreight.Currency);
        Assert.AreEqual(1900m, offer.OceanFreight.Amount);
    }

    [TestMethod]
    public void Parse_ShouldExtractAllIn()
    {
        var offer = new MaerskOfferParser().Parse(LoadFixture()).Offers
            .Single(x => x.ExternalRouteId == "ROUTE-001");

        Assert.IsNotNull(offer.AllIn);
        Assert.AreEqual("USD", offer.AllIn.Currency);
        Assert.AreEqual(2500m, offer.AllIn.Amount);
    }

    [TestMethod]
    public void Parse_ShouldExtractRouteSchedule()
    {
        var offer = new MaerskOfferParser().Parse(LoadFixture()).Offers
            .Single(x => x.ExternalRouteId == "ROUTE-001");

        Assert.AreEqual(new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero), offer.Etd);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 18, 8, 0, 0, TimeSpan.Zero), offer.Eta);
        Assert.AreEqual(27, offer.TransitDays);
        Assert.AreEqual(1, offer.Legs.Count);
    }

    [TestMethod]
    public void Parse_ShouldExtractVesselAndVoyage()
    {
        var offer = new MaerskOfferParser().Parse(LoadFixture()).Offers
            .Single(x => x.ExternalRouteId == "ROUTE-001");

        Assert.AreEqual("MAERSK TEST", offer.Vessel);
        Assert.AreEqual("123W", offer.Voyage);
    }

    [TestMethod]
    public void Parse_ShouldGroupProductsByRouteId()
    {
        var result = new MaerskOfferParser().Parse(LoadFixture());

        Assert.AreEqual(2, result.Offers.Count);
        var offer = result.Offers.Single(x => x.ExternalRouteId == "ROUTE-001");
        CollectionAssert.AreEquivalent(
            new[] { "MaerskSpot", "MaerskSpotWithVR" },
            offer.Products.ToArray());
    }

    [TestMethod]
    public void Parse_ShouldNotDuplicateSpotAndSpotWithVR()
    {
        var result = new MaerskOfferParser().Parse(LoadFixture());

        Assert.AreEqual(2, result.Offers.Count);
        Assert.AreEqual(
            1,
            result.Offers.Count(x => x.ExternalRouteId == "ROUTE-001"));
    }

    [TestMethod]
    public void Parse_ShouldKeepEveryOfferedSailingAndItsAvailability()
    {
        const string json = """
        [
          {
            "routeId": "SEARCH_1_1",
            "status": "OFFERED",
            "availabilityFlag": false,
            "productDataCollection": [
              {
                "productReference": "MaerskSpot",
                "dataBundle": [
                  {
                    "dataType": "ROUTE_SCHEDULE",
                    "data": {
                      "schedules": [
                        {
                          "originDepartureDatetime": "2026-10-02T09:30:00",
                          "destinationArrivalDatetime": "2026-11-04T01:00:00",
                          "sailing": {
                            "vessel": { "name": "MAERSK TOKYO" },
                            "voyageNumber": "638E"
                          }
                        }
                      ]
                    }
                  },
                  {
                    "dataType": "PRICE_BREAKDOWN",
                    "data": {
                      "totalAmount": { "unit": "USD", "value": 8682.0 },
                      "totalBasicFreightAmount": { "unit": "USD", "value": 8100.0 }
                    }
                  }
                ]
              }
            ]
          },
          {
            "routeId": "SEARCH_1_2",
            "status": "OFFERED",
            "availabilityFlag": true,
            "productDataCollection": [
              {
                "productReference": "MaerskSpot",
                "dataBundle": [
                  {
                    "dataType": "ROUTE_SCHEDULE",
                    "data": {
                      "schedules": [
                        {
                          "originDepartureDatetime": "2026-10-09T19:00:00",
                          "destinationArrivalDatetime": "2026-11-25T01:00:00",
                          "sailing": {
                            "vessel": { "name": "MAERSK EUREKA" },
                            "voyageNumber": "641E"
                          }
                        }
                      ]
                    }
                  },
                  {
                    "dataType": "PRICE_BREAKDOWN",
                    "data": {
                      "totalAmount": { "unit": "USD", "value": 8732.0 },
                      "totalBasicFreightAmount": { "unit": "USD", "value": 8150.0 }
                    }
                  }
                ]
              }
            ]
          }
        ]
        """;

        var result = new MaerskOfferParser().Parse(json);

        Assert.AreEqual(2, result.Offers.Count);

        var first = result.Offers.Single(x => x.ExternalRouteId == "SEARCH_1_1");
        Assert.IsFalse(first.Available);
        Assert.AreEqual(8100m, first.OceanFreight?.Amount);
        Assert.AreEqual("MAERSK TOKYO", first.Vessel);

        var second = result.Offers.Single(x => x.ExternalRouteId == "SEARCH_1_2");
        Assert.IsTrue(second.Available);
        Assert.AreEqual(8150m, second.OceanFreight?.Amount);
        Assert.AreEqual("MAERSK EUREKA", second.Vessel);
    }

    [TestMethod]
    public void Parse_ShouldExtractCurrentMaersk202PayloadShape()
    {
        const string json = """
        [
          {
            "routeId": "REAL-1",
            "status": "OFFERED",
            "availabilityFlag": true,
            "selectedProducts": [
              { "productName": "Maersk Spot" }
            ],
            "productDataCollection": [
              {
                "productReference": "MaerskSpot",
                "dataBundle": [
                  {
                    "dataType": "DEADLINES",
                    "data": {
                      "deadlines": [
                        {
                          "code": "CCC",
                          "date": "2026-10-08T10:00:00"
                        }
                      ]
                    }
                  },
                  {
                    "dataType": "ROUTE_SCHEDULE",
                    "data": {
                      "transitTime": "67440",
                      "schedules": [
                        {
                          "originDepartureDatetime": "2026-10-09T19:00:00",
                          "destinationArrivalDatetime": "2026-11-15T15:00:00",
                          "startLocation": { "cityName": "Shanghai" },
                          "endLocation": { "cityName": "Balboa" },
                          "sailing": {
                            "vessel": { "name": "MAERSK EUREKA" },
                            "voyageNumber": "641E"
                          }
                        },
                        {
                          "originDepartureDatetime": "2026-11-22T13:00:00",
                          "destinationArrivalDatetime": "2026-11-25T01:00:00",
                          "startLocation": { "cityName": "Balboa" },
                          "endLocation": { "cityName": "Puerto Caldera" },
                          "sailing": {
                            "vessel": { "name": "AS Savanna" },
                            "voyageNumber": "647W"
                          }
                        }
                      ]
                    }
                  },
                  {
                    "dataType": "PRICE_BREAKDOWN",
                    "data": {
                      "charges": [
                        {
                          "chargeApplicationCode": "Freight",
                          "chargeTypeCode": "BAS",
                          "chargeTypeName": "Basic Ocean Freight",
                          "amount": { "unit": "USD", "value": 8150.0 }
                        },
                        {
                          "chargeApplicationCode": "Destination",
                          "chargeTypeCode": "DHC",
                          "chargeTypeName": "Terminal Handling Service - Destination",
                          "amount": { "unit": "USD", "value": 245.0 }
                        }
                      ],
                      "totalAmount": { "unit": "USD", "value": 8732.0 },
                      "totalBasicFreightAmount": { "unit": "USD", "value": 8150.0 }
                    }
                  }
                ]
              }
            ]
          }
        ]
        """;

        var offer = new MaerskOfferParser().Parse(json).Offers.Single();

        Assert.AreEqual("USD", offer.OceanFreight?.Currency);
        Assert.AreEqual(8150m, offer.OceanFreight?.Amount);
        Assert.AreEqual("USD", offer.AllIn?.Currency);
        Assert.AreEqual(8732m, offer.AllIn?.Amount);
        Assert.AreEqual(2, offer.Charges.Count);
        Assert.AreEqual(2, offer.Legs.Count);
        Assert.AreEqual("Shanghai", offer.Legs.First().From);
        Assert.AreEqual("Puerto Caldera", offer.Legs.Last().To);
        Assert.AreEqual("MAERSK EUREKA", offer.Vessel);
        Assert.AreEqual("641E", offer.Voyage);
        Assert.AreEqual(
            new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero),
            offer.CargoCutoff);
        Assert.AreEqual(
            new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero),
            offer.Etd);
        Assert.AreEqual(
            new DateTimeOffset(2026, 11, 25, 1, 0, 0, TimeSpan.Zero),
            offer.Eta);
        Assert.AreEqual(47, offer.TransitDays);
    }

    [TestMethod]
    public void Parse_ShouldSupportAlternativeMaerskFieldNames()
    {
        const string json = """
        {
          "offers": [
            {
              "status": "OFFERED",
              "routeId": "ALT-001",
              "productDataCollection": [
                {
                  "productDisplayName": "Maersk Spot",
                  "currencyIsoCode": "USD",
                  "basicFreightAmount": 3210.50,
                  "totalPrice": 3500.00,
                  "departureDate": "2026-10-02T10:00:00Z",
                  "arrivalDate": "2026-10-29T10:00:00Z",
                  "transitDays": 27,
                  "vesselName": "MAERSK ALT",
                  "voyage": "ALT123"
                }
              ]
            }
          ]
        }
        """;

        var offer = new MaerskOfferParser().Parse(json).Offers.Single();

        Assert.AreEqual(3210.50m, offer.OceanFreight?.Amount);
        Assert.AreEqual("USD", offer.OceanFreight?.Currency);
        Assert.AreEqual(3500m, offer.AllIn?.Amount);
        Assert.AreEqual(27, offer.TransitDays);
        Assert.AreEqual("MAERSK ALT", offer.Vessel);
        Assert.AreEqual("ALT123", offer.Voyage);
        Assert.IsNotNull(offer.Etd);
        Assert.IsNotNull(offer.Eta);
    }
}
