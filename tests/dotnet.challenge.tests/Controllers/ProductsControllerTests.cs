using dotnet.challenge.api.Controllers;
using dotnet.challenge.api.Models;
using dotnet.challenge.data.Entities;
using dotnet.challenge.tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace dotnet.challenge.tests.Controllers;

/// <summary>
/// Unit tests for <see cref="ProductsController"/> in isolation: they check how service results map to
/// HTTP results. Model validation (400) runs in the MVC pipeline, so it is covered by ApiTests instead.
/// </summary>
public class ProductsControllerTests
{
    private readonly FakeInventoryService _inventory = new();
    private readonly ProductsController _controller;

    public ProductsControllerTests()
    {
        _controller = new ProductsController(_inventory);
    }

    [Fact]
    public async Task CreateProduct_Returns201_PointingAtGetProduct()
    {
        var product = new Product { Id = Guid.NewGuid(), Name = "Widget", Stock = 10 };
        _inventory.Product = product;
        using var cts = new CancellationTokenSource();

        var result = await _controller.CreateProduct(new CreateProductRequest { Name = "Widget", Stock = 10 }, cts.Token);

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        created.ActionName.Should().Be(nameof(ProductsController.GetProduct));
        created.RouteValues!["id"].Should().Be(product.Id);
        created.Value.Should().Be(product);

        _inventory.CreatedWith.Should().Be(("Widget", 10));
        _inventory.LastToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task GetProduct_Existing_Returns200WithProduct()
    {
        var product = new Product { Id = Guid.NewGuid(), Name = "Widget", Stock = 3 };
        _inventory.Product = product;

        var result = await _controller.GetProduct(product.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(product);
        _inventory.LastId.Should().Be(product.Id);
    }

    [Fact]
    public async Task GetProduct_Unknown_Returns404()
    {
        _inventory.Product = null;

        var result = await _controller.GetProduct(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetProduct_ForwardsCancellationToken()
    {
        using var cts = new CancellationTokenSource();

        await _controller.GetProduct(Guid.NewGuid(), cts.Token);

        _inventory.LastToken.Should().Be(cts.Token);
    }
}
