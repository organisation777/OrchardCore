using System;
using Fluid;
using Fluid.Values;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using OrchardCore.ContentManagement;
using OrchardCore.ContentManagement.Display.ContentDisplay;
using OrchardCore.Data.Migration;
using OrchardCore.DisplayManagement.Descriptors;
using OrchardCore.DisplayManagement.Liquid.Filters;
using OrchardCore.Indexing;
using OrchardCore.Liquid.Drivers;
using OrchardCore.Liquid.Filters;
using OrchardCore.Liquid.Handlers;
using OrchardCore.Liquid.Indexing;
using OrchardCore.Liquid.Models;
using OrchardCore.Liquid.Services;
using OrchardCore.Modules;

namespace OrchardCore.Liquid
{
    public class Startup : StartupBase
    {
        public override void ConfigureServices(IServiceCollection services)
        {
            services.AddScoped<ISlugService, SlugService>();
            services.AddScoped<ILiquidTemplateManager, LiquidTemplateManager>();

            services.Configure<TemplateOptions>(options =>
            {
                options.Filters.AddFilter("t", LiquidViewFilters.Localize);
                options.Filters.AddFilter("html_class", LiquidViewFilters.HtmlClass);
                options.Filters.AddFilter("shape_new", LiquidViewFilters.NewShape);
                options.Filters.AddFilter("shape_render", LiquidViewFilters.ShapeRender);
                options.Filters.AddFilter("shape_stringify", LiquidViewFilters.ShapeStringify);
                options.Filters.AddFilter("shape_properties", LiquidViewFilters.ShapeProperties);

                options.Filters.AddFilter("local", TimeZoneFilter.Local);
                options.Filters.AddFilter("slugify", SlugifyFilter.Slugify);

                // When a property of a JObject value is accessed, try to look into its properties
                options.MemberAccessStrategy.Register<JObject, object>((source, name) => source[name]);

                // Convert JToken to FluidValue
                options.ValueConverters.Add(x => x is JObject o ? new ObjectValue(o) : null);
                options.ValueConverters.Add(x => x is JValue v ? v.Value : null);
                options.ValueConverters.Add(x => x is DateTime d ? new ObjectValue(d) : null);

                options.Filters.AddFilter("href", ContentUrlFilter.Href);
                options.Filters.AddFilter("absolute_url", AbsoluteUrlFilter.AbsoluteUrl);
                options.Filters.AddFilter("liquid", LiquidFilter.Liquid);
                options.Filters.AddFilter("json", JsonFilter.Json);
                options.Filters.AddFilter("jsonparse", JsonParseFilter.JsonParse);
            });            
        }
    }

    [RequireFeatures("OrchardCore.Contents")]
    public class LiquidPartStartup : StartupBase
    {
        public override void ConfigureServices(IServiceCollection services)
        {
            // Liquid Part
            services.AddScoped<IShapeTableProvider, LiquidShapes>();
            services.AddContentPart<LiquidPart>()
                .UseDisplayDriver<LiquidPartDisplayDriver>()
                .AddHandler<LiquidPartHandler>();

            services.AddScoped<IDataMigration, Migrations>();
            services.AddScoped<IContentPartIndexHandler, LiquidPartIndexHandler>();
        }
    }

    [RequireFeatures("OrchardCore.Shortcodes")]
    public class ShortcodesStartup : StartupBase
    {
        public override void ConfigureServices(IServiceCollection services)
        {
            services.Configure<TemplateOptions>(o => o.Filters.AddFilter("shortcode", ShortcodeFilter.Shortcode));
        }
    }
}
