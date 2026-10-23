#nullable enable
using BazaarGameShared.Domain.Runs;
using System.Globalization;
using System.Reflection;
using System.Text;
using BazaarGameClient.Domain.Models.Cards;
using BazaarGameClient.Domain.Tooltips;
using BazaarGameShared.Domain.Cards;
using BazaarGameShared.Domain.Cards.Enchantments;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Values;
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Infrastructure.Logging;
using TheBazaar;
using TheBazaar.Tooltips;

namespace BazaarPlusPlus.Game.ItemEnchantPreview.Preview;

public static class ItemEnchantPreviewRenderer
{
    private static readonly MethodInfo CardTooltipRenderMethod = typeof(CardTooltipData).GetMethod(
        "RenderTooltip",
        BindingFlags.Instance | BindingFlags.NonPublic,
        null,
        new[] { typeof(TooltipBuilder) },
        null
    );

    public static List<TooltipSegment> Render(ItemCard previewCard, TEnchantment enchantment)
    {
        var segments = new List<TooltipSegment>();
        if (
            enchantment.Localization?.Tooltips == null
            || enchantment.Localization.Tooltips.Count == 0
        )
            return segments;

        foreach (var tooltip in enchantment.Localization.Tooltips)
        {
            var content = tooltip?.Content;
            if (content == null)
                continue;

            var renderedText = RenderTooltipText(previewCard, content);
            if (string.IsNullOrWhiteSpace(renderedText))
                continue;

            segments.Add(
                ItemEnchantPreviewFormatting.CreateSegment(
                    previewCard.Enchantment.ToManagedNullable() ?? EEnchantmentType.Heavy,
                    renderedText
                )
            );
        }

        return segments;
    }

    private static string RenderTooltipText(ItemCard previewCard, TLocalizableText content)
    {
        var enchantment = previewCard.Enchantment.ToManagedNullable() ?? EEnchantmentType.Heavy;
        var localized = GetLocalizedText(content, enchantment);
        if (string.IsNullOrWhiteSpace(localized))
            return string.Empty;

        try
        {
            return RenderWithCardTooltipData(previewCard, localized).TrimEnd();
        }
        catch (Exception ex)
        {
            BppLog.WarnEvent(
                new BppLogEvent(
                    BppLogFeatureScope.ItemEnchantPreview,
                    "item_enchant_preview.render.degraded",
                    storm: ["stage", "reason_code"]
                ),
                ex,
                new BppLogField("stage", ItemEnchantRenderStage.CardTooltipData),
                new BppLogField("reason_code", ItemEnchantLogReasonCode.RenderFallback),
                new BppLogField("enchantment", enchantment)
            );
            try
            {
                var builder = TooltipBuilder.Create(
                    CreateTooltipContext(
                        previewCard,
                        previewCard.Template!,
                        new ValueContext(Data.Run?.TryCast<IRun>(), previewCard.TryCast<ICard>())
                    ),
                    localized
                );

                return RenderTooltipBuilder(builder).TrimEnd();
            }
            catch (Exception innerEx)
            {
                BppLog.WarnEvent(
                    new BppLogEvent(
                        BppLogFeatureScope.ItemEnchantPreview,
                        "item_enchant_preview.render.degraded",
                        storm: ["stage", "reason_code"]
                    ),
                    innerEx,
                    ("stage", ItemEnchantRenderStage.TooltipBuilder),
                    ("reason_code", ItemEnchantLogReasonCode.RawTextFallback),
                    new BppLogField("enchantment", enchantment)
                );
                return localized;
            }
        }
    }

    private static string RenderWithCardTooltipData(ItemCard previewCard, string localized)
    {
        var builder = TooltipBuilder.Create(
            CreateTooltipContext(
                previewCard,
                previewCard.Template!,
                new ValueContext(Data.Run?.TryCast<IRun>(), previewCard.TryCast<ICard>())
            ),
            localized
        );

        if (CardTooltipRenderMethod == null)
            return RenderTooltipBuilder(builder);

        var tooltipData = new CardTooltipData(previewCard, previewCard.Template!);
        var rendered =
            CardTooltipRenderMethod.Invoke(tooltipData, new object[] { builder }) as string;
        return string.IsNullOrWhiteSpace(rendered) ? RenderTooltipBuilder(builder) : rendered;
    }

    private static TooltipContext CreateTooltipContext(
        Card instance,
        ITCard template,
        ValueContext valueContext
    )
    {
        var constructor = typeof(TooltipContext).GetConstructor(
            new[] { typeof(Card), typeof(ITCard), typeof(ValueContext) }
        );
        if (constructor != null)
            return (TooltipContext)
                constructor.Invoke(new object[] { instance, template, valueContext });

        var context = default(TooltipContext);
        object boxed = context;
        SetTooltipContextField(boxed, nameof(TooltipContext.Instance), instance);
        SetTooltipContextField(boxed, nameof(TooltipContext.Template), template);
        SetTooltipContextField(boxed, nameof(TooltipContext.ValueContext), valueContext);
        return (TooltipContext)boxed;
    }

    private static void SetTooltipContextField(object context, string fieldName, object value)
    {
        var field = typeof(TooltipContext).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.Public
        );
        field?.SetValue(context, value);
    }

    private static string GetLocalizedText(TLocalizableText content, EEnchantmentType enchantment)
    {
        try
        {
            return content.GetLocalizedText();
        }
        catch (Exception ex)
        {
            BppLog.WarnEvent(
                new BppLogEvent(
                    BppLogFeatureScope.ItemEnchantPreview,
                    "item_enchant_preview.render.degraded",
                    storm: ["stage", "reason_code"]
                ),
                ex,
                ("stage", ItemEnchantRenderStage.Localization),
                ("reason_code", ItemEnchantLogReasonCode.LocalizationFallback),
                new BppLogField("enchantment", enchantment)
            );
            return content.Text ?? string.Empty;
        }
    }

    private static string RenderTooltipBuilder(TooltipBuilder builder)
    {
        var rendered = new StringBuilder();
        foreach (var component in builder.Components)
        {
            if (
                component.TryCast<ITooltipToken>() is { } token
                && token.ReferencedAttribute.ToManagedNullable() is { } refAttr
                && refAttr.RequiresConversionToSeconds()
            )
            {
                var seconds = TooltipExtensions.MillisecondsToSeconds(
                    token.Resolve().ToManagedNullable() ?? 0f
                );
                rendered.Append(
                    seconds.IsDecimal()
                        ? seconds.GetDecimalValueString()
                        : seconds.ToString(CultureInfo.CurrentCulture)
                );
            }
            else
            {
                rendered.Append(component.Render());
            }
        }

        return rendered.ToString();
    }
}
