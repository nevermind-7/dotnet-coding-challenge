using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using dotnet.challenge.api;
using dotnet.challenge.data.Cache;
using dotnet.challenge.data.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace dotnet.challenge.tests;

/// <summary>
/// End-to-end HTTP tests: status codes, Location headers, validation and JSON shape.
/// </summary>
public class ApiTests : IClassFixture<ApiTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Time);
            });
        }
    }

    private readonly Factory _factory;
    private readonly HttpClient _client;

    public ApiTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<JsonElement> CreateProductAsync(int stock = 5)
    {
        var response = await _client.PostAsJsonAsync("/api/products", new { name = "Widget", stock });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<HttpResponseMessage> ReserveAsync(object body) => _client.PostAsJsonAsync("/api/reservations", body);

    private async Task<JsonElement> CreateReservationAsync(string productId, int quantity = 1, int ttlSeconds = 60)
    {
        var response = await ReserveAsync(new { productId, quantity, ttlSeconds });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<int> StockOf(string productId) =>
        (await _client.GetFromJsonAsync<JsonElement>($"/api/products/{productId}")).GetProperty("stock").GetInt32();

    private async Task<string> StatusOf(string reservationId) =>
        (await _client.GetFromJsonAsync<JsonElement>($"/api/reservations/{reservationId}")).GetProperty("status").GetString()!;

    [Fact]
    public async Task CreateProduct_Returns201_WithLocationThatResolves()
    {
        var response = await _client.PostAsJsonAsync("/api/products", new { name = "Widget", stock = 7 });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();

        var fetched = await _client.GetAsync(response.Headers.Location);
        fetched.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await fetched.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("name").GetString().Should().Be("Widget");
        body.GetProperty("stock").GetInt32().Should().Be(7);
    }

    [Theory]
    [InlineData("{\"stock\":5}")]
    [InlineData("{\"name\":\"\",\"stock\":5}")]
    [InlineData("{\"name\":\"Widget\",\"stock\":-1}")]
    [InlineData("not json")]
    public async Task CreateProduct_InvalidBody_Returns400(string json)
    {
        var response = await _client.PostAsync("/api/products",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateProduct_NameTooLong_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/products", new { name = new string('x', 129), stock = 1 });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetUnknownProduct_Returns404()
    {
        (await _client.GetAsync($"/api/products/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateReservation_Returns201_WithLocation_AndStringStatus()
    {
        var product = await CreateProductAsync(stock: 5);
        var productId = product.GetProperty("id").GetString()!;

        var response = await ReserveAsync(new { productId, quantity = 2, ttlSeconds = 60 });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("Active");
        body.GetProperty("quantity").GetInt32().Should().Be(2);

        (await _client.GetAsync(response.Headers.Location)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StockOf(productId)).Should().Be(3);
    }

    [Fact]
    public async Task CreateReservation_MissingProductId_Returns400()
    {
        var response = await ReserveAsync(new { quantity = 1, ttlSeconds = 60 });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(-1, 60)]
    [InlineData(1, 0)]
    [InlineData(1, 3601)]
    public async Task CreateReservation_OutOfRangeValues_Return400(int quantity, int ttlSeconds)
    {
        var product = await CreateProductAsync();
        var response = await ReserveAsync(new { productId = product.GetProperty("id").GetString(), quantity, ttlSeconds });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateReservation_UnknownProduct_Returns404()
    {
        var response = await ReserveAsync(new { productId = Guid.NewGuid(), quantity = 1, ttlSeconds = 60 });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateReservation_InsufficientStock_Returns409_AndLeavesStockUntouched()
    {
        var product = await CreateProductAsync(stock: 1);
        var productId = product.GetProperty("id").GetString()!;

        var response = await ReserveAsync(new { productId, quantity = 2, ttlSeconds = 60 });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await StockOf(productId)).Should().Be(1);
    }

    [Fact]
    public async Task Confirm_Returns200_AndIsIdempotent()
    {
        var product = await CreateProductAsync();
        var id = (await CreateReservationAsync(product.GetProperty("id").GetString()!)).GetProperty("id").GetString();

        var first = await _client.PostAsync($"/api/reservations/{id}/confirm", null);
        var second = await _client.PostAsync($"/api/reservations/{id}/confirm", null);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString().Should().Be("Confirmed");
    }

    [Fact]
    public async Task Release_Returns204_IsIdempotent_AndRestoresStockOnce()
    {
        var product = await CreateProductAsync(stock: 5);
        var productId = product.GetProperty("id").GetString()!;
        var id = (await CreateReservationAsync(productId, quantity: 3)).GetProperty("id").GetString();

        (await _client.DeleteAsync($"/api/reservations/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _client.DeleteAsync($"/api/reservations/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await StockOf(productId)).Should().Be(5);
        (await StatusOf(id!)).Should().Be("Released");
    }

    [Fact]
    public async Task Confirm_AfterRelease_Returns409()
    {
        var product = await CreateProductAsync();
        var id = (await CreateReservationAsync(product.GetProperty("id").GetString()!)).GetProperty("id").GetString();
        await _client.DeleteAsync($"/api/reservations/{id}");

        (await _client.PostAsync($"/api/reservations/{id}/confirm", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task UnknownReservation_Returns404_ForGetConfirmAndRelease()
    {
        var id = Guid.NewGuid();
        (await _client.GetAsync($"/api/reservations/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.PostAsync($"/api/reservations/{id}/confirm", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.DeleteAsync($"/api/reservations/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Expiry_IsObservedOnGet_AndConfirmThenFailsWith409()
    {
        var product = await CreateProductAsync(stock: 4);
        var productId = product.GetProperty("id").GetString()!;
        var id = (await CreateReservationAsync(productId, quantity: 4, ttlSeconds: 5)).GetProperty("id").GetString()!;

        // Note: the factory's clock is shared by this class's tests; advancing it only
        // affects reservations created before this point, which are all independent.
        _factory.Time.Advance(TimeSpan.FromSeconds(6));

        (await StatusOf(id)).Should().Be("Expired");
        (await StockOf(productId)).Should().Be(4);
        (await _client.PostAsync($"/api/reservations/{id}/confirm", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _client.DeleteAsync($"/api/reservations/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await StockOf(productId)).Should().Be(4);
    }

    // Rule 2, "the transition happens automatically": the real hosted sweeper must expire the
    // reservation with no client reading it. State is inspected straight from the cache, because
    // an API GET would expire it lazily and hide whether the sweeper ran.
    [Fact]
    public async Task Sweeper_ExpiresOverdueReservation_WithoutAnyClientRead()
    {
        var product = await CreateProductAsync(stock: 6);
        var productId = Guid.Parse(product.GetProperty("id").GetString()!);
        var reservationId = Guid.Parse(
            (await CreateReservationAsync(productId.ToString(), quantity: 6, ttlSeconds: 5)).GetProperty("id").GetString()!);

        _factory.Time.Advance(TimeSpan.FromSeconds(6));

        var reservations = _factory.Services.GetRequiredService<ISimpleObjectCache<Guid, Reservation>>();
        var products = _factory.Services.GetRequiredService<ISimpleObjectCache<Guid, Product>>();

        // The sweeper ticks every second of real time; allow a generous margin for slow CI machines.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((await reservations.GetAsync(reservationId))!.Status != ReservationStatus.Expired
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        (await reservations.GetAsync(reservationId))!.Status.Should().Be(ReservationStatus.Expired);
        (await products.GetAsync(productId))!.Stock.Should().Be(6);
    }
}
