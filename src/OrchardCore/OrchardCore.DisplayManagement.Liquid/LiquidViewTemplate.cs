using System;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Fluid;
using Fluid.Accessors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Localization;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OrchardCore.DisplayManagement.Layout;
using OrchardCore.DisplayManagement.Shapes;
using OrchardCore.Environment.Shell.Scope;
using OrchardCore.Liquid;

namespace OrchardCore.DisplayManagement.Liquid
{
    public class LiquidViewTemplate
    {
        public static LiquidViewParser Parser = new LiquidViewParser();
        
        public static readonly string ViewsFolder = "Views";
        public static readonly string ViewExtension = ".liquid";
        public static readonly MemoryCache Cache = new MemoryCache(new MemoryCacheOptions());
        public IFluidTemplate FluidTemplate { get; }

        public LiquidViewTemplate(IFluidTemplate fluidTemplate)
        {
            FluidTemplate = fluidTemplate;
        }

        /// <summary>
        /// Retrieve the current <see cref="LiquidTemplateContext"/> from the current shell scope.
        /// </summary>
        public static LiquidTemplateContext Context => ShellScope.GetOrCreateFeature(() => new LiquidTemplateContext(ShellScope.Services));

        internal static async Task RenderAsync(RazorPage<dynamic> page)
        {
            var services = page.Context.RequestServices;

            var path = Path.ChangeExtension(page.ViewContext.ExecutingFilePath, ViewExtension);
            var templateOptions = services.GetRequiredService<TemplateOptions>();
            var isDevelopment = services.GetRequiredService<IHostEnvironment>().IsDevelopment();

            var template = await ParseAsync(path, templateOptions.FileProvider, Cache, isDevelopment);

            var context = Context;
            var htmlEncoder = services.GetRequiredService<HtmlEncoder>();

            try
            {
                await context.EnterScopeAsync(page.ViewContext, (object)page.Model, scopeAction: null);
                await template.FluidTemplate.RenderAsync(page.Output, htmlEncoder, context);
            }
            finally
            {
                context.ReleaseScope();
            }
        }

        public static Task<LiquidViewTemplate> ParseAsync(string path, IFileProvider fileProvider, IMemoryCache cache, bool isDevelopment)
        {
            return cache.GetOrCreateAsync(path, async entry =>
            {
                entry.SetSlidingExpiration(TimeSpan.FromHours(1));
                var fileInfo = fileProvider.GetFileInfo(path);

                if (isDevelopment)
                {
                    entry.ExpirationTokens.Add(fileProvider.Watch(path));
                }

                using (var stream = fileInfo.CreateReadStream())
                {
                    using (var sr = new StreamReader(stream))
                    {
                        if (Parser.TryParse(await sr.ReadToEndAsync(), out var template, out var errors))
                        {
                            return new LiquidViewTemplate(template);
                        }
                        else
                        {
                            throw new Exception($"Failed to parse liquid file {path}: {String.Join(System.Environment.NewLine, errors)}");
                        }
                    }
                }
            });
        }
    }

    internal class ShapeAccessor : DelegateAccessor
    {
        public ShapeAccessor() : base(_getter)
        {
        }

        private static Func<object, string, object> _getter => (o, n) =>
        {
            if (o is Shape shape)
            {
                if (shape.Properties.TryGetValue(n, out var result))
                {
                    return result;
                }

                if (n == "Items")
                {
                    return shape.Items;
                }

                // Resolves Model.Content.MyType-MyField-FieldType_Display__DisplayMode
                var namedShaped = shape.Named(n);
                if (namedShaped != null)
                {
                    return namedShaped;
                }

                // Resolves Model.Content.MyNamedPart
                // Resolves Model.Content.MyType__MyField
                // Resolves Model.Content.MyType-MyField
                return shape.NormalizedNamed(n.Replace("__", "-"));
            }

            return null;
        };
    }

    public static class LiquidViewTemplateExtensions
    {
        public static async Task<string> RenderAsync(this LiquidViewTemplate template, TextEncoder encoder, LiquidTemplateContext context, object model, Action<Scope> scopeAction)
        {
            var viewContextAccessor = context.Services.GetRequiredService<ViewContextAccessor>();
            var viewContext = viewContextAccessor.ViewContext;

            if (viewContext == null)
            {
                viewContext = viewContextAccessor.ViewContext = await GetViewContextAsync(context);
            }

            try
            {
                await context.EnterScopeAsync(viewContext, model, scopeAction);
                return await template.FluidTemplate.RenderAsync(context, encoder);
            }
            finally
            {
                context.ReleaseScope();
            }
        }

        public static async Task RenderAsync(this LiquidViewTemplate template, TextWriter writer, TextEncoder encoder, LiquidTemplateContext context, object model, Action<Scope> scopeAction)
        {
            var viewContextAccessor = context.Services.GetRequiredService<ViewContextAccessor>();
            var viewContext = viewContextAccessor.ViewContext;

            if (viewContext == null)
            {
                viewContext = viewContextAccessor.ViewContext = await GetViewContextAsync(context);
            }

            try
            {
                await context.EnterScopeAsync(viewContext, model, scopeAction);
                await template.FluidTemplate.RenderAsync(writer, encoder, context);
            }
            finally
            {
                context.ReleaseScope();
            }
        }

        public static async Task<ViewContext> GetViewContextAsync(LiquidTemplateContext context)
        {
            var actionContext = context.Services.GetService<IActionContextAccessor>()?.ActionContext;

            if (actionContext == null)
            {
                var httpContext = context.Services.GetRequiredService<IHttpContextAccessor>().HttpContext;
                actionContext = await GetActionContextAsync(httpContext);
            }

            return GetViewContext(actionContext);
        }

        internal static async Task<ActionContext> GetActionContextAsync(HttpContext httpContext)
        {
            var routeData = new RouteData();
            routeData.Routers.Add(new RouteCollection());

            var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());
            var filters = httpContext.RequestServices.GetServices<IAsyncViewActionFilter>();

            foreach (var filter in filters)
            {
                await filter.OnActionExecutionAsync(actionContext);
            }

            return actionContext;
        }

        internal static ViewContext GetViewContext(ActionContext actionContext)
        {
            var services = actionContext.HttpContext.RequestServices;

            var options = services.GetService<IOptions<MvcViewOptions>>();
            var viewEngine = options.Value.ViewEngines[0];

            var viewResult = viewEngine.GetView(executingFilePath: null,
                LiquidViewsFeatureProvider.DefaultRazorViewPath, isMainPage: true);

            var tempDataProvider = services.GetService<ITempDataProvider>();

            var viewContext = new ViewContext(
                actionContext,
                viewResult.View,
                new ViewDataDictionary(
                    metadataProvider: new EmptyModelMetadataProvider(),
                    modelState: new ModelStateDictionary()),
                new TempDataDictionary(
                    actionContext.HttpContext,
                    tempDataProvider),
                TextWriter.Null,
                new HtmlHelperOptions());

            if (viewContext.View is RazorView razorView)
            {
                razorView.RazorPage.ViewContext = viewContext;
            }

            return viewContext;
        }
    }

    public static class LiquidTemplateContextExtensions
    {
        internal static async Task EnterScopeAsync(this LiquidTemplateContext context, ViewContext viewContext, object model, Action<Scope> scopeAction)
        {
            if (!context.IsInitialized)
            {
                context.ViewContext = viewContext;

                var displayHelper = context.Services.GetRequiredService<IDisplayHelper>();
                context.AmbientValues.Add("DisplayHelper", displayHelper);

                var urlHelperFactory = context.Services.GetRequiredService<IUrlHelperFactory>();
                var urlHelper = urlHelperFactory.GetUrlHelper(viewContext);
                context.AmbientValues.Add("UrlHelper", urlHelper);

                var shapeFactory = context.Services.GetRequiredService<IShapeFactory>();
                context.AmbientValues.Add("ShapeFactory", shapeFactory);

                var layoutAccessor = context.Services.GetRequiredService<ILayoutAccessor>();
                var layout = await layoutAccessor.GetLayoutAsync();
                context.AmbientValues.Add("ThemeLayout", layout);

                foreach (var handler in context.Services.GetServices<ILiquidTemplateEventHandler>())
                {
                    await handler.RenderingAsync(context);
                }

                context.CultureInfo = CultureInfo.CurrentUICulture;

                context.IsInitialized = true;
            }

            context.EnterChildScope();

            var viewLocalizer = context.Services.GetRequiredService<IViewLocalizer>();

            if (viewLocalizer is IViewContextAware contextable)
            {
                contextable.Contextualize(viewContext);
            }

            context.SetValue("ViewLocalizer", viewLocalizer);

            //if (model != null)
            //{
            //    context.MemberAccessStrategy.Register(model.GetType());
            //}

            if (context.GetValue("Model")?.ToObjectValue() == model && model is IShape shape)
            {
                if (contextInternal.ShapeRecursions++ > LiquidTemplateContext.MaxShapeRecursions)
                {
                    throw new InvalidOperationException(
                        $"The '{shape.Metadata.Type}' shape has been called recursively more than {LiquidTemplateContext.MaxShapeRecursions} times.");
                }
            }
            else
            {
                contextInternal.ShapeRecursions = 0;
            }

            context.SetValue("Model", model);

            scopeAction?.Invoke(context.LocalScope);
        }
    }
}
