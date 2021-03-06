// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information. 

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.JSInterop;

namespace Microsoft.AspNetCore.Components.Web.Virtualization
{
    /// <summary>
    /// Provides functionality for rendering a virtualized list of items.
    /// </summary>
    /// <typeparam name="TItem">The <c>context</c> type for the items being rendered.</typeparam>
    public sealed class Virtualize<TItem> : ComponentBase, IVirtualizeJsCallbacks, IAsyncDisposable
    {
        private VirtualizeJsInterop? _jsInterop;

        private ElementReference _spacerBefore;

        private ElementReference _spacerAfter;

        private int _itemsBefore;

        private int _visibleItemCapacity;

        private int _itemCount;

        private int _loadedItemsStartIndex;

        private IEnumerable<TItem>? _loadedItems;

        private CancellationTokenSource? _refreshCts;

        private Exception? _refreshException;

        private ItemsProviderDelegate<TItem> _itemsProvider = default!;

        private RenderFragment<TItem>? _itemTemplate;

        private RenderFragment<PlaceholderContext>? _placeholder;

        [Inject]
        private IJSRuntime JSRuntime { get; set; } = default!;

        /// <summary>
        /// Gets or sets the item template for the list.
        /// </summary>
        [Parameter]
        public RenderFragment<TItem>? ChildContent { get; set; }

        /// <summary>
        /// Gets or sets the item template for the list.
        /// </summary>
        [Parameter]
        public RenderFragment<TItem>? ItemContent { get; set; }

        /// <summary>
        /// Gets or sets the template for items that have not yet been loaded in memory.
        /// </summary>
        [Parameter]
        public RenderFragment<PlaceholderContext>? Placeholder { get; set; }

        /// <summary>
        /// Gets the size of each item in pixels.
        /// </summary>
        [Parameter]
        public float ItemSize { get; set; }

        /// <summary>
        /// Gets or sets the function providing items to the list.
        /// </summary>
        [Parameter]
        public ItemsProviderDelegate<TItem>? ItemsProvider { get; set; }

        /// <summary>
        /// Gets or sets the fixed item source.
        /// </summary>
        [Parameter]
        public ICollection<TItem>? Items { get; set; }

        /// <inheritdoc />
        protected override void OnParametersSet()
        {
            if (ItemSize <= 0)
            {
                throw new InvalidOperationException(
                    $"{GetType()} requires a positive value for parameter '{nameof(ItemSize)}' to perform virtualization.");
            }

            if (ItemsProvider != null)
            {
                if (Items != null)
                {
                    throw new InvalidOperationException(
                        $"{GetType()} can only accept one item source from its parameters. " +
                        $"Do not supply both '{nameof(Items)}' and '{nameof(ItemsProvider)}'.");
                }

                _itemsProvider = ItemsProvider;
            }
            else if (Items != null)
            {
                _itemsProvider = DefaultItemsProvider;
            }
            else
            {
                throw new InvalidOperationException(
                    $"{GetType()} requires either the '{nameof(Items)}' or '{nameof(ItemsProvider)}' parameters to be specified " +
                    $"and non-null.");
            }

            _itemTemplate = ItemContent ?? ChildContent;
            _placeholder = Placeholder ?? DefaultPlaceholder;
        }

        /// <inheritdoc />
        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                _jsInterop = new VirtualizeJsInterop(this, JSRuntime);
                await _jsInterop.InitializeAsync(_spacerBefore, _spacerAfter);
            }
        }

        /// <inheritdoc />
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (_refreshException != null)
            {
                var oldRefreshException = _refreshException;
                _refreshException = null;

                throw oldRefreshException;
            }

            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "style", GetSpacerStyle(_itemsBefore));
            builder.AddElementReferenceCapture(2, elementReference => _spacerBefore = elementReference);
            builder.CloseElement();

            var lastItemIndex = Math.Min(_itemsBefore + _visibleItemCapacity, _itemCount);
            var renderIndex = _itemsBefore;
            var placeholdersBeforeCount = Math.Min(_loadedItemsStartIndex, lastItemIndex);

            builder.OpenRegion(3);

            // Render placeholders before the loaded items.
            for (; renderIndex < placeholdersBeforeCount; renderIndex++)
            {
                // This is a rare case where it's valid for the sequence number to be programmatically incremented.
                // This is only true because we know for certain that no other content will be alongside it.
                builder.AddContent(renderIndex, _placeholder, new PlaceholderContext(renderIndex));
            }

            builder.CloseRegion();

            // Render the loaded items.
            if (_loadedItems != null && _itemTemplate != null)
            {
                var itemsToShow = _loadedItems
                    .Skip(_itemsBefore - _loadedItemsStartIndex)
                    .Take(lastItemIndex - _loadedItemsStartIndex);

                builder.OpenRegion(4);

                foreach (var item in itemsToShow)
                {
                    _itemTemplate(item)(builder);
                    renderIndex++;
                }

                builder.CloseRegion();
            }

            builder.OpenRegion(5);

            // Render the placeholders after the loaded items.
            for (; renderIndex < lastItemIndex; renderIndex++)
            {
                builder.AddContent(renderIndex, _placeholder, new PlaceholderContext(renderIndex));
            }

            builder.CloseRegion();

            var itemsAfter = Math.Max(0, _itemCount - _visibleItemCapacity - _itemsBefore);

            builder.OpenElement(6, "div");
            builder.AddAttribute(7, "style", GetSpacerStyle(itemsAfter));
            builder.AddElementReferenceCapture(8, elementReference => _spacerAfter = elementReference);

            builder.CloseElement();
        }

        private string GetSpacerStyle(int itemsInSpacer)
            => $"height: {itemsInSpacer * ItemSize}px;";

        void IVirtualizeJsCallbacks.OnBeforeSpacerVisible(float spacerSize, float containerSize)
        {
            CalcualteItemDistribution(spacerSize, containerSize, out var itemsBefore, out var visibleItemCapacity);

            UpdateItemDistribution(itemsBefore, visibleItemCapacity);
        }

        void IVirtualizeJsCallbacks.OnAfterSpacerVisible(float spacerSize, float containerSize)
        {
            CalcualteItemDistribution(spacerSize, containerSize, out var itemsAfter, out var visibleItemCapacity);

            var itemsBefore = Math.Max(0, _itemCount - itemsAfter - visibleItemCapacity);

            UpdateItemDistribution(itemsBefore, visibleItemCapacity);
        }

        private void CalcualteItemDistribution(float spacerSize, float containerSize, out int itemsInSpacer, out int visibleItemCapacity)
        {
            itemsInSpacer = Math.Max(0, (int)Math.Floor(spacerSize / ItemSize) - 1);
            visibleItemCapacity = (int)Math.Ceiling(containerSize / ItemSize) + 2;
        }

        private void UpdateItemDistribution(int itemsBefore, int visibleItemCapacity)
        {
            if (itemsBefore != _itemsBefore || visibleItemCapacity != _visibleItemCapacity)
            {
                _itemsBefore = itemsBefore;
                _visibleItemCapacity = visibleItemCapacity;
                var refreshTask = RefreshDataAsync();

                if (!refreshTask.IsCompleted)
                {
                    StateHasChanged();
                }
            }
        }

        private async Task RefreshDataAsync()
        {
            _refreshCts?.Cancel();
            _refreshCts = new CancellationTokenSource();

            var cancellationToken = _refreshCts.Token;
            var request = new ItemsProviderRequest(_itemsBefore, _visibleItemCapacity, cancellationToken);

            try
            {
                var result = await _itemsProvider(request);

                // Only apply result if the task was not canceled.
                if (!cancellationToken.IsCancellationRequested)
                {
                    _itemCount = result.TotalItemCount;
                    _loadedItems = result.Items;
                    _loadedItemsStartIndex = request.StartIndex;

                    StateHasChanged();
                }
            }
            catch (Exception e)
            {
                if (e is OperationCanceledException oce && oce.CancellationToken == cancellationToken)
                {
                    // No-op; we canceled the operation, so it's fine to suppress this exception.
                }
                else
                {
                    // Cache this exception so the renderer can throw it.
                    _refreshException = e;

                    // Re-render the component to throw the exception.
                    StateHasChanged();
                }
            }
        }

        private ValueTask<ItemsProviderResult<TItem>> DefaultItemsProvider(ItemsProviderRequest request)
        {
            return ValueTask.FromResult(new ItemsProviderResult<TItem>(
                Items!.Skip(request.StartIndex).Take(request.Count),
                Items!.Count));
        }

        private RenderFragment DefaultPlaceholder(PlaceholderContext context) => (builder) =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "style", $"height: {ItemSize}px;");
            builder.CloseElement();
        };

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            _refreshCts?.Cancel();

            if (_jsInterop != null)
            {
                await _jsInterop.DisposeAsync();
            }
        }
    }
}
