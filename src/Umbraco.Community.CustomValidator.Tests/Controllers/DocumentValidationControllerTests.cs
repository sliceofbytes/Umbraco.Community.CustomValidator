using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Community.CustomValidator.Controllers;
using Umbraco.Community.CustomValidator.Enums;
using Umbraco.Community.CustomValidator.Interfaces;
using Umbraco.Community.CustomValidator.Models;
using Umbraco.Community.CustomValidator.Services;
using Umbraco.Community.CustomValidator.Validation;

namespace Umbraco.Community.CustomValidator.Tests.Controllers;

[TestFixture]
public sealed class DocumentValidationControllerTests
{
    private CustomValidationService _validationExecutor = null!;
    private Mock<IUmbracoContextAccessor> _umbracoContextAccessorMock = null!;
    private Mock<ILogger<DocumentValidationController>> _loggerMock = null!;
    private DocumentValidationController _sut = null!;

    private ServiceProvider _serviceProvider = null!;

    [SetUp]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        services.AddMemoryCache();

        var options = new CustomValidatorOptions
        {
            CacheExpirationMinutes = 30,
            TreatWarningsAsErrors = false
        };
        var optionsMock = new Mock<IOptions<CustomValidatorOptions>>();
        optionsMock.Setup(x => x.Value).Returns(options);

        _serviceProvider = services.BuildServiceProvider();

        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var logger = new Mock<ILogger<ValidatorLookup>>();
        var lookup = new ValidatorLookup([], logger.Object);

        var customValidatorRegistry = new CustomValidatorRegistry(
            scopeFactory,
            lookup,
            _serviceProvider.GetRequiredService<ILogger<CustomValidatorRegistry>>());

        var hybridCache = _serviceProvider.GetRequiredService<HybridCache>();

        var cacheService = new CustomValidationCacheService(
            hybridCache,
            optionsMock.Object,
            _serviceProvider.GetRequiredService<ILogger<CustomValidationCacheService>>());

        var variationContextAccessorMock = new Mock<IVariationContextAccessor>();
        var languageServiceMock = new Mock<ILanguageService>();
        languageServiceMock.Setup(x => x.GetDefaultIsoCodeAsync())
            .ReturnsAsync("en-GB");

        var statusCache = new CustomValidationStatusCache(
            _serviceProvider.GetRequiredService<IMemoryCache>(),
            optionsMock.Object,
            _serviceProvider.GetRequiredService<ILogger<CustomValidationStatusCache>>());

        _validationExecutor = new CustomValidationService(
            customValidatorRegistry,
            cacheService,
            statusCache,
            optionsMock.Object,
            variationContextAccessorMock.Object,
            languageServiceMock.Object,
            _serviceProvider.GetRequiredService<ILogger<CustomValidationService>>());

        _umbracoContextAccessorMock = new Mock<IUmbracoContextAccessor>();
        _loggerMock = new Mock<ILogger<DocumentValidationController>>();

        _sut = new DocumentValidationController(
            _umbracoContextAccessorMock.Object,
            _validationExecutor,
            _loggerMock.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider.Dispose();
        _sut.Dispose();
    }

    #region Content Found Tests

    [Test]
    public async Task ValidateDocument_WithContent_ReturnsOkResult()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var content = CreateMockPublishedContent(documentId);

        SetupUmbracoContext(content);

        // Act
        var result = await _sut.ValidateDocument(documentId, "en-US");

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());

        var okResult = result as OkObjectResult;
        var response = okResult!.Value as ValidationResponse;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.ContentId, Is.EqualTo(documentId));
    }

    [Test]
    public async Task ValidateDocument_WithValidator_ReturnsValidationResult()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var content = CreateMockPublishedContent(documentId);

        var validationExecutor = CreateValidationExecutorWithValidator();
        var sut = new DocumentValidationController(
            _umbracoContextAccessorMock.Object,
            validationExecutor,
            _loggerMock.Object);

        SetupUmbracoContext(content);

        // Act
        var result = await sut.ValidateDocument(documentId, "en-US");

        // Assert
        var okResult = result as OkObjectResult;
        var response = okResult!.Value as ValidationResponse;

        Assert.That(response!.HasValidator, Is.True);
        Assert.That(response.Messages?.Count(), Is.EqualTo(1));
    }

    #endregion

    #region Content Not Found Tests

    [Test]
    public async Task ValidateDocument_ContentNotFound_Returns404()
    {
        // Arrange
        var documentId = Guid.NewGuid();

        SetupUmbracoContext(null); // Content not found

        // Act
        var result = await _sut.ValidateDocument(documentId, null);

        // Assert
        Assert.That(result, Is.InstanceOf<ObjectResult>());

        var objectResult = result as ObjectResult;
        Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));

        var problemDetails = objectResult.Value as ProblemDetails;
        Assert.That(problemDetails!.Detail, Does.Contain("could not be found"));
    }

    [Test]
    public async Task ValidateDocument_ContentNotFound_DoesNotCallValidator()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var validatorCalled = false;

        var validationExecutor = CreateValidationExecutorWithTrackingValidator(() => validatorCalled = true);
        var sut = new DocumentValidationController(
            _umbracoContextAccessorMock.Object,
            validationExecutor,
            _loggerMock.Object);

        SetupUmbracoContext(null);

        // Act
        await sut.ValidateDocument(documentId, null);

        // Assert
        Assert.That(validatorCalled, Is.False, "Validator should not be called when content not found");
    }

    #endregion

    #region Exception Handling Tests

    [Test]
    public async Task ValidateDocument_UmbracoContextThrows_Returns500()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var umbracoContext = (IUmbracoContext?)null;

        _umbracoContextAccessorMock.Setup(x => x.TryGetUmbracoContext(out umbracoContext))
            .Returns(false); // This will cause GetRequiredUmbracoContext to throw

        // Act
        var result = await _sut.ValidateDocument(documentId, null);

        // Assert
        var objectResult = result as ObjectResult;
        Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status500InternalServerError));
    }

    [Test]
    public async Task ValidateDocument_ExceptionThrown_LogsError()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var umbracoContext = (IUmbracoContext?)null;

        _umbracoContextAccessorMock.Setup(x => x.TryGetUmbracoContext(out umbracoContext))
            .Returns(false);

        // Act
        await _sut.ValidateDocument(documentId, null);

        // Assert
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Unexpected error")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion

    #region Culture Parameter Tests

    [Test]
    public async Task ValidateDocument_WithCulture_PassesCultureToExecutor()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var content = CreateMockPublishedContent(documentId);

        SetupUmbracoContext(content);

        // Act
        var result = await _sut.ValidateDocument(documentId, "da-DK");

        // Assert - Just verify it worked (culture handling is tested in ValidationExecutorTests)
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task ValidateDocument_WithNullCulture_PassesNullToExecutor()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var content = CreateMockPublishedContent(documentId);

        SetupUmbracoContext(content);

        // Act
        var result = await _sut.ValidateDocument(documentId, null);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    #endregion

    #region Helper Methods

    private void SetupUmbracoContext(IPublishedContent? content)
    {
        var umbracoContextMock = new Mock<IUmbracoContext>();
        var contentCacheMock = new Mock<IPublishedContentCache>();

        contentCacheMock.Setup(x => x.GetById(true, It.IsAny<Guid>())).Returns(content);
        contentCacheMock.Setup(x => x.GetById(It.IsAny<bool>(), It.IsAny<Guid>())).Returns(content);

        umbracoContextMock.Setup(x => x.Content).Returns(contentCacheMock.Object);

        var context = umbracoContextMock.Object;
        _umbracoContextAccessorMock.Setup(x => x.TryGetUmbracoContext(out context))
            .Returns(true);
    }

    private static IPublishedContent CreateMockPublishedContent(Guid key)
    {
        var contentTypeMock = new Mock<IPublishedContentType>();
        contentTypeMock.Setup(x => x.Alias).Returns("testPage");
        contentTypeMock.Setup(x => x.ItemType).Returns(PublishedItemType.Content);

        var contentMock = new Mock<IPublishedContent>();
        contentMock.Setup(x => x.Id).Returns(1);
        contentMock.Setup(x => x.Key).Returns(key);
        contentMock.Setup(x => x.Name).Returns("Test Page");
        contentMock.Setup(x => x.ContentType).Returns(contentTypeMock.Object);

        return contentMock.Object;
    }

    private CustomValidationService CreateValidationExecutorWithValidator()
    {
        var metadata = new List<ValidatorMetadata>
        {
            new() { ValidatorType = typeof(TestValidator), ContentType = typeof(IPublishedContent) }
        };

        var logger = new Mock<ILogger<ValidatorLookup>>();
        var lookup = new ValidatorLookup(metadata, logger.Object);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        services.AddMemoryCache();
        services.AddSingleton<TestValidator>();

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var validatorRegistry = new CustomValidatorRegistry(
            scopeFactory,
            lookup,
            sp.GetRequiredService<ILogger<CustomValidatorRegistry>>());

        var options = new CustomValidatorOptions { CacheExpirationMinutes = 30 };
        var optionsMock = new Mock<IOptions<CustomValidatorOptions>>();
        optionsMock.Setup(x => x.Value).Returns(options);

        var cacheService = new CustomValidationCacheService(
            sp.GetRequiredService<HybridCache>(),
            optionsMock.Object,
            sp.GetRequiredService<ILogger<CustomValidationCacheService>>());

        var variationContextMock = new Mock<IVariationContextAccessor>();
        var languageServiceMock = new Mock<ILanguageService>();

        var statusCacheWithValidator = new CustomValidationStatusCache(
            sp.GetRequiredService<IMemoryCache>(),
            optionsMock.Object,
            sp.GetRequiredService<ILogger<CustomValidationStatusCache>>());

        return new CustomValidationService(
            validatorRegistry,
            cacheService,
            statusCacheWithValidator,
            optionsMock.Object,
            variationContextMock.Object,
            languageServiceMock.Object,
            sp.GetRequiredService<ILogger<CustomValidationService>>());
    }

    private CustomValidationService CreateValidationExecutorWithTrackingValidator(Action onValidate)
    {

        var metadata = new List<ValidatorMetadata>
        {
            new() { ValidatorType = typeof(TrackingValidator), ContentType = typeof(IPublishedContent) }
        };

        var logger = new Mock<ILogger<ValidatorLookup>>();
        var lookup = new ValidatorLookup(metadata, logger.Object);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        services.AddMemoryCache();

        var validator = new TrackingValidator(onValidate);
        services.AddSingleton(validator);
        services.AddSingleton<IDocumentValidator>(sp => validator);


        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var options = new CustomValidatorOptions { CacheExpirationMinutes = 30 };
        var optionsMock = new Mock<IOptions<CustomValidatorOptions>>();
        optionsMock.Setup(x => x.Value).Returns(options);

        var validatorRegistry = new CustomValidatorRegistry(
            scopeFactory,
            lookup,
            sp.GetRequiredService<ILogger<CustomValidatorRegistry>>());

        var cacheService = new CustomValidationCacheService(
            sp.GetRequiredService<HybridCache>(),
            optionsMock.Object,
            sp.GetRequiredService<ILogger<CustomValidationCacheService>>());

        var variationContextMock = new Mock<IVariationContextAccessor>();
        var languageServiceMock = new Mock<ILanguageService>();

        var statusCacheTracking = new CustomValidationStatusCache(
            sp.GetRequiredService<IMemoryCache>(),
            optionsMock.Object,
            sp.GetRequiredService<ILogger<CustomValidationStatusCache>>());

        return new CustomValidationService(
            validatorRegistry,
            cacheService,
            statusCacheTracking,
            optionsMock.Object,
            variationContextMock.Object,
            languageServiceMock.Object,
            sp.GetRequiredService<ILogger<CustomValidationService>>());
    }

    #endregion

    #region Test Classes

    private class TestValidator : IDocumentValidator
    {
        public Task<IEnumerable<ValidationMessage>> ValidateAsync(IPublishedContent content)
        {
            return Task.FromResult<IEnumerable<ValidationMessage>>(new List<ValidationMessage>
            {
                new() { Message = "Test validation", Severity = ValidationSeverity.Info }
            });
        }
    }

    private class TrackingValidator : IDocumentValidator
    {
        private readonly Action _onValidate;

        public TrackingValidator(Action onValidate)
        {
            _onValidate = onValidate;
        }

        public Task<IEnumerable<ValidationMessage>> ValidateAsync(IPublishedContent content)
        {
            _onValidate();
            return Task.FromResult<IEnumerable<ValidationMessage>>(new List<ValidationMessage>());
        }
    }

    #endregion
}