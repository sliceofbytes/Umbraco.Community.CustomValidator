using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Umbraco.Cms.Api.Management.ViewModels.Document;
using Umbraco.Cms.Api.Management.ViewModels.Document.Collection;
using Umbraco.Cms.Api.Management.ViewModels.Document.Item;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Community.CustomValidator.Enums;
using Umbraco.Community.CustomValidator.Services;
using Umbraco.Community.CustomValidator.Validation;

namespace Umbraco.Community.CustomValidator.Tests.Services;

using Umbraco.Cms.Api.Management.ViewModels.Tree;
using Umbraco.Cms.Core.Models.PublishedContent;

[TestFixture]
public sealed class CustomValidationErrorFlagProviderTests
{
    private const string FlagAlias = "CustomValidator.ValidationErrorsFlag";

    private MemoryCache _memoryCache = null!;
    private CustomValidationStatusCache _statusCache = null!;
    private Mock<IOptions<CustomValidatorOptions>> _optionsMock = null!;
    private CustomValidatorOptions _options = null!;
    private CustomValidationErrorFlagProvider _sut = null!;
    private ServiceProvider _serviceProvider = null!;
    private ServiceProvider _resolverServiceProvider = null!;

    [SetUp]
    public void Setup()
    {
        _options = new CustomValidatorOptions
        {
            CacheExpirationMinutes = 30,
            TreatWarningsAsErrors = false,
            EntityFlagMode = ValidationFlagMode.Lazy
        };

        _optionsMock = new Mock<IOptions<CustomValidatorOptions>>();
        _optionsMock.Setup(x => x.Value).Returns(_options);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        _serviceProvider = services.BuildServiceProvider();

        _memoryCache = new MemoryCache(new MemoryCacheOptions());

        _statusCache = new CustomValidationStatusCache(
            _memoryCache,
            _optionsMock.Object,
            _serviceProvider.GetRequiredService<ILogger<CustomValidationStatusCache>>());

        var scopeFactory = BuildResolverScopeFactory();

        _sut = new CustomValidationErrorFlagProvider(
            scopeFactory,
            _optionsMock.Object,
            _serviceProvider.GetRequiredService<ILogger<CustomValidationErrorFlagProvider>>());
    }

    [TearDown]
    public void TearDown()
    {
        _memoryCache.Dispose();
        _serviceProvider.Dispose();
        _resolverServiceProvider?.Dispose();
    }

    #region CanProvideFlags Tests

    [Test]
    public void CanProvideFlags_DocumentTreeItemResponseModel_ReturnsTrue()
    {
        Assert.That(_sut.CanProvideFlags<DocumentTreeItemResponseModel>(), Is.True);
    }

    [Test]
    public void CanProvideFlags_DocumentCollectionResponseModel_ReturnsTrue()
    {
        Assert.That(_sut.CanProvideFlags<DocumentCollectionResponseModel>(), Is.True);
    }

    [Test]
    public void CanProvideFlags_DocumentItemResponseModel_ReturnsTrue()
    {
        Assert.That(_sut.CanProvideFlags<DocumentItemResponseModel>(), Is.True);
    }

    [Test]
    public void CanProvideFlags_UnknownType_ReturnsFalse()
    {
        Assert.That(_sut.CanProvideFlags<UnsupportedItem>(), Is.False);
    }

    #endregion

    #region PopulateFlagsAsync - Mode Tests

    [Test]
    public async Task PopulateFlagsAsync_ModeNone_ReturnsEarlyWithNoFlags()
    {
        _options.EntityFlagMode = ValidationFlagMode.None;
        var documentId = Guid.NewGuid();
        _statusCache.SetStatus(documentId, ValidationStatus.HasErrors);

        var item = CreateTreeItem(documentId, "en-US");
        await _sut.PopulateFlagsAsync(new[] { item });

        Assert.That(item.Variants.Single().Flags, Is.Empty);
    }

    [Test]
    public async Task PopulateFlagsAsync_EmptyList_DoesNotThrow()
    {
        await _sut.PopulateFlagsAsync(Enumerable.Empty<DocumentTreeItemResponseModel>());
    }

    [Test]
    public async Task PopulateFlagsAsync_ItemWithEmptyGuid_IsSkipped()
    {
        var item = CreateTreeItem(Guid.Empty, "en-US");
        await _sut.PopulateFlagsAsync(new[] { item });

        Assert.That(item.Variants.Single().Flags, Is.Empty);
    }

    #endregion

    #region PopulateFlagsAsync - Flag Assignment Tests

    [Test]
    public async Task PopulateFlagsAsync_StatusHasErrors_AddsFlagToVariant()
    {
        var documentId = Guid.NewGuid();
        _statusCache.SetStatus(documentId, ValidationStatus.HasErrors, "en-US");

        var item = CreateTreeItem(documentId, "en-US");
        await _sut.PopulateFlagsAsync(new[] { item });

        var flags = item.Variants.Single().Flags.ToList();
        Assert.That(flags, Has.Count.EqualTo(1));
        Assert.That(flags[0].Alias, Is.EqualTo(FlagAlias));
    }

    [Test]
    public async Task PopulateFlagsAsync_StatusValid_DoesNotAddFlag()
    {
        var documentId = Guid.NewGuid();
        _statusCache.SetStatus(documentId, ValidationStatus.Valid, "en-US");

        var item = CreateTreeItem(documentId, "en-US");
        await _sut.PopulateFlagsAsync(new[] { item });

        Assert.That(item.Variants.Single().Flags, Is.Empty);
    }

    [Test]
    public async Task PopulateFlagsAsync_StatusUnknown_DoesNotAddFlag()
    {
        var documentId = Guid.NewGuid();
        // No status set — Unknown; Lazy mode returns false for Unknown

        var item = CreateTreeItem(documentId, "en-US");
        await _sut.PopulateFlagsAsync(new[] { item });

        Assert.That(item.Variants.Single().Flags, Is.Empty);
    }

    [Test]
    public async Task PopulateFlagsAsync_InvariantContent_NullCulture_AddsFlagWhenHasErrors()
    {
        var documentId = Guid.NewGuid();
        _statusCache.SetStatus(documentId, ValidationStatus.HasErrors, null);

        var item = CreateTreeItem(documentId, (string?)null);
        await _sut.PopulateFlagsAsync(new[] { item });

        Assert.That(item.Variants.Single().Flags.Count(), Is.EqualTo(1));
    }

    #endregion

    #region PopulateFlagsAsync - Multi-Variant / Multi-Item Tests

    [Test]
    public async Task PopulateFlagsAsync_MultipleVariants_FlagsAppliedPerCulture()
    {
        var documentId = Guid.NewGuid();
        _statusCache.SetStatus(documentId, ValidationStatus.HasErrors, "en-US");
        _statusCache.SetStatus(documentId, ValidationStatus.Valid, "da-DK");

        var item = CreateTreeItem(documentId, "en-US", "da-DK");
        await _sut.PopulateFlagsAsync(new[] { item });

        var variants = item.Variants.ToList();
        Assert.That(variants[0].Flags.Count(), Is.EqualTo(1), "en-US should be flagged");
        Assert.That(variants[1].Flags, Is.Empty, "da-DK should not be flagged");
    }

    [Test]
    public async Task PopulateFlagsAsync_MultipleItems_EachFlaggedIndependently()
    {
        var doc1 = Guid.NewGuid();
        var doc2 = Guid.NewGuid();
        _statusCache.SetStatus(doc1, ValidationStatus.HasErrors, "en-US");
        _statusCache.SetStatus(doc2, ValidationStatus.Valid, "en-US");

        var item1 = CreateTreeItem(doc1, "en-US");
        var item2 = CreateTreeItem(doc2, "en-US");
        await _sut.PopulateFlagsAsync(new[] { item1, item2 });

        Assert.That(item1.Variants.Single().Flags.Count(), Is.EqualTo(1));
        Assert.That(item2.Variants.Single().Flags, Is.Empty);
    }

    [Test]
    public async Task PopulateFlagsAsync_VariantsNotReplaced_WhenNoStatusChange()
    {
        var documentId = Guid.NewGuid();
        _statusCache.SetStatus(documentId, ValidationStatus.Valid, "en-US");

        var originalVariant = CreateVariant("en-US");
        var item = CreateTreeItemWithVariants(documentId, new[] { originalVariant });

        await _sut.PopulateFlagsAsync(new[] { item });

        Assert.That(item.Variants, Contains.Item(originalVariant));
    }

    [Test]
    public async Task PopulateFlagsAsync_ItemWithNoVariants_DoesNotThrow()
    {
        var documentId = Guid.NewGuid();
        var item = CreateTreeItemWithVariants(documentId, []);

        Assert.DoesNotThrowAsync(() =>
            _sut.PopulateFlagsAsync(new[] { item }));
    }

    #endregion

    #region Helper Methods

    private IServiceScopeFactory BuildResolverScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();

        var lookup = new ValidatorLookup([], new Mock<ILogger<ValidatorLookup>>().Object);

        services.AddSingleton(lookup);
        services.AddSingleton<CustomValidatorRegistry>();
        services.AddSingleton(_optionsMock.Object);
        services.AddSingleton(_memoryCache);
        services.AddSingleton(_statusCache);

        services.AddSingleton<CustomValidationCacheService>();
        services.AddSingleton<CustomValidationStatusCache>(sp => _statusCache);

        var variationContextMock = new Mock<IVariationContextAccessor>();
        var languageServiceMock = new Mock<ILanguageService>();
        languageServiceMock.Setup(x => x.GetDefaultIsoCodeAsync()).ReturnsAsync("en-US");
        services.AddSingleton(variationContextMock.Object);
        services.AddSingleton(languageServiceMock.Object);
        services.AddSingleton(new Mock<IUmbracoContextAccessor>().Object);

        services.AddScoped<CustomValidationService>();
        services.AddScoped<CustomValidationFlagStatusResolver>();

        _resolverServiceProvider = services.BuildServiceProvider();
        return _resolverServiceProvider.GetRequiredService<IServiceScopeFactory>();
    }

    private static DocumentVariantItemResponseModel CreateVariant(string? culture) =>
        new()
        {
            Name = culture ?? "Invariant",
            State = DocumentVariantState.Draft,
            Culture = culture
        };

    private static DocumentTreeItemResponseModel CreateTreeItem(Guid documentId, params string?[] cultures)
    {
        var variants = cultures.Select(CreateVariant).ToArray();
        return CreateTreeItemWithVariants(documentId, variants);
    }

    private static DocumentTreeItemResponseModel CreateTreeItemWithVariants(
        Guid documentId,
        IEnumerable<DocumentVariantItemResponseModel> variants) =>
        new()
        {
            Id = documentId,
            Variants = variants
        };

    #endregion

    #region Unsupported Type

    private class UnsupportedItem : Umbraco.Cms.Api.Management.ViewModels.IHasFlags
    {
        public Guid Id => Guid.Empty;
        public IEnumerable<Umbraco.Cms.Api.Management.ViewModels.FlagModel> Flags { get; set; } = [];
        public void AddFlag(string alias) { }
        public void RemoveFlag(string alias) { }
    }

    #endregion
}
