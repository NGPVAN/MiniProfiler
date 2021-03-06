﻿namespace StackExchange.Profiling.UI
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Web;
    using System.Web.Routing;
    using System.Web.SessionState;

    using StackExchange.Profiling.Helpers;

    /// <summary>
    /// Understands how to route and respond to MiniProfiler UI URLS.
    /// </summary>
    public class MiniProfilerHandler : IRouteHandler, IHttpHandler, IRequiresSessionState
    {
        /// <summary>
        /// Embedded resource contents keyed by filename.
        /// </summary>
        private static readonly ConcurrentDictionary<string, string> ResourceCache = new ConcurrentDictionary<string, string>();

        /// <summary>
        /// The bypass local load.
        /// </summary>
        private static bool bypassLocalLoad = false;

        /// <summary>
        /// Gets a value indicating whether to keep things static and reusable.
        /// </summary>
        public bool IsReusable
        {
            get { return true; }
        }

        /// <summary>
        /// Usually called internally, sometimes you may clear the routes during the apps lifecycle, if you do that call this to bring back mini profiler.
        /// </summary>
        public static void RegisterRoutes()
        {
            var routes = RouteTable.Routes;
            var handler = new MiniProfilerHandler();
            var prefix = MiniProfiler.Settings.RouteBasePath.Replace("~/", string.Empty).EnsureTrailingSlash();

            using (routes.GetWriteLock())
            {
                var route = new Route(prefix + "{filename}", handler)
                                {
                                    // we have to specify these, so no MVC route helpers will match, e.g. @Html.ActionLink("Home", "Index", "Home")
                                    Defaults = new RouteValueDictionary(new { controller = "MiniProfilerHandler", action = "ProcessRequest" }),
                                    Constraints = new RouteValueDictionary(new { controller = "MiniProfilerHandler", action = "ProcessRequest" })
                                };

                // put our routes at the beginning, like a boss
                routes.Insert(0, route);
            }
        }

        /// <summary>
        /// Returns this <see cref="MiniProfilerHandler"/> to handle <paramref name="requestContext"/>.
        /// </summary>
        /// <param name="requestContext">
        /// The request Context.
        /// </param>
        /// <returns>the http handler implementation</returns>
        public IHttpHandler GetHttpHandler(RequestContext requestContext)
        {
            return this; // elegant? I THINK SO.
        }

        /// <summary>
        /// Returns either includes' <c>css/javascript</c> or results' html.
        /// </summary>
        /// <param name="context">The http context.</param>
        public void ProcessRequest(HttpContext context)
        {
            string output;
            string path = context.Request.AppRelativeCurrentExecutionFilePath;

            switch (Path.GetFileNameWithoutExtension(path).ToLowerInvariant())
            {
                case "jquery.1.7.1":
                case "jquery.tmpl":
                case "includes":
                case "list":
                    output = Includes(context, path);
                    break;

                case "results-index":
                    output = Index(context);
                    break;

                case "results-list":
                    output = ResultList(context);
                    break;

                case "results":
                    output = Results(context);
                    break;

                default:
                    output = NotFound(context);
                    break;
            }

            context.Response.Write(output);
        }

        /// <summary>
        /// Renders script tag found in "include.partial.html" - this is shared with all other language implementations, so if you change it, you MUST
        /// provide changes for those other implementations, e.g. ruby.
        /// </summary>
        internal static HtmlString RenderIncludes(
            MiniProfiler profiler,
            RenderPosition? position = null,
            bool? showTrivial = null,
            bool? showTimeWithChildren = null,
            int? maxTracesToShow = null,
            bool? showControls = null,
            bool? startHidden = null)
        {
            if (profiler == null) return new HtmlString("");

            MiniProfiler.Settings.EnsureStorageStrategy();
            var authorized = MiniProfiler.Settings.Results_Authorize == null || MiniProfiler.Settings.Results_Authorize(HttpContext.Current.Request);

            // unviewed ids are added to this list during Storage.Save, but we know we haven't see the current one yet, so go ahead and add it to the end 
            var ids = authorized ? MiniProfiler.Settings.Storage.GetUnviewedIds(profiler.User) : new List<Guid>();
            ids.Add(profiler.Id);

            var format = GetResource("include.partial.html");
            var result = format.Format(new
            {
                path = VirtualPathUtility.ToAbsolute(MiniProfiler.Settings.RouteBasePath).EnsureTrailingSlash(),
                version = MiniProfiler.Settings.Version,
                ids = string.Join(",", ids.Select(guid => guid.ToString())),
                position = (position ?? MiniProfiler.Settings.PopupRenderPosition).ToString().ToLower(),
                showTrivial = (showTrivial ?? MiniProfiler.Settings.PopupShowTrivial).ToJs(),
                showChildren = (showTimeWithChildren ?? MiniProfiler.Settings.PopupShowTimeWithChildren).ToJs(),
                maxTracesToShow = maxTracesToShow ?? MiniProfiler.Settings.PopupMaxTracesToShow,
                showControls = (showControls ?? MiniProfiler.Settings.ShowControls).ToJs(),
                currentId = profiler.Id,
                authorized = authorized.ToJs(),
                toggleShortcut = MiniProfiler.Settings.PopupToggleKeyboardShortcut,
                startHidden = (startHidden ?? MiniProfiler.Settings.PopupStartHidden).ToJs()
            });

            return new HtmlString(result);
        }

        /// <summary>
        /// The result list.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <returns>a string containing the result list.</returns>
        private static string ResultList(HttpContext context)
        {
            string message;
            if (!AuthorizeRequest(context, isList: true, message: out message))
            {
                return message;
            }

            var lastId = context.Request["last-id"];
            Guid lastGuid = Guid.Empty;

            if (!lastId.IsNullOrWhiteSpace())
            {
                Guid.TryParse(lastId, out lastGuid);
            }

            // After app restart, MiniProfiler.Settings.Storage will be null if no results saved, and NullReferenceException is thrown.
            if (MiniProfiler.Settings.Storage == null)
            {
                MiniProfiler.Settings.EnsureStorageStrategy();
            }

            var guids = MiniProfiler.Settings.Storage.List(100);

            if (lastGuid != Guid.Empty)
            {
                guids = guids.TakeWhile(g => g != lastGuid);
            }

            guids = guids.Reverse();

            return guids.Select(
                g =>
                {
                    var profiler = MiniProfiler.Settings.Storage.Load(g);
                    return
                        new
                            {
                                profiler.Id,
                                profiler.Name,
                                profiler.DurationMilliseconds,
                                profiler.DurationMillisecondsInSql,
                                profiler.ClientTimings,
                                profiler.Started,
                                profiler.ExecutedNonQueries,
                                profiler.ExecutedReaders,
                                profiler.ExecutedScalars,
                                profiler.HasAllTrivialTimings,
                                profiler.HasDuplicateSqlTimings,
                                profiler.HasSqlTimings,
                                profiler.HasTrivialTimings,
                                profiler.HasUserViewed,
                                profiler.MachineName,
                                profiler.User
                            };
                }).ToJson();
        }

        /// <summary>
        /// the index (Landing) view.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <returns>a string containing the html.</returns>
        private static string Index(HttpContext context)
        {
            string message;
            if (!AuthorizeRequest(context, isList: true, message: out message))
            {
                return message;
            }

            context.Response.ContentType = "text/html";

            var path = VirtualPathUtility.ToAbsolute(MiniProfiler.Settings.RouteBasePath).EnsureTrailingSlash();
            return new StringBuilder()
                .AppendLine("<html><head>")
                .AppendFormat("<title>List of profiling sessions</title>")
                .AppendLine()
                .AppendLine("<script type='text/javascript' src='" + path + "jquery.1.7.1.js?v=" + MiniProfiler.Settings.Version + "'></script>")
                .AppendLine("<script id='mini-profiler' data-ids='' type='text/javascript' src='" + path + "includes.js?v=" + MiniProfiler.Settings.Version + "'></script>")
                .AppendLine("<script type='text/javascript' src='" + path + "jquery.tmpl.js?v=" + MiniProfiler.Settings.Version + "'></script>")
                .AppendLine(
                    "<script type='text/javascript' src='" + path + "list.js?v=" + MiniProfiler.Settings.Version
                    + "'></script>")
                .AppendLine(
                    "<link href='" + path + "list.css?v=" + MiniProfiler.Settings.Version
                    + "' rel='stylesheet' type='text/css'>")
                .AppendLine(
                    "<script type='text/javascript'>MiniProfiler.list.init({path: '" + path + "', version: '"
                    + MiniProfiler.Settings.Version + "'})</script>")
                .AppendLine("</head><body></body></html>")
                .ToString();
        }

        /// <summary>
        /// Handles rendering static content files.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="path">The path.</param>
        /// <returns>a string containing the content type.</returns>
        private static string Includes(HttpContext context, string path)
        {
            var response = context.Response;
            switch (Path.GetExtension(path))
            {
                case ".js":
                    response.ContentType = "application/javascript";
                    break;
                case ".css":
                    response.ContentType = "text/css";
                    break;
                case ".tmpl":
                    response.ContentType = "text/x-jquery-tmpl";
                    break;
                default:
                    return NotFound(context);
            }
#if !DEBUG
            var cache = response.Cache;
            cache.SetCacheability(System.Web.HttpCacheability.Public);
            cache.SetExpires(DateTime.Now.AddDays(7));
            cache.SetValidUntilExpires(true);
#endif

            var embeddedFile = Path.GetFileName(path);
            return GetResource(embeddedFile);
        }

        /// <summary>
        /// Handles rendering a previous <c>MiniProfiler</c> session, identified by its <c>"?id=GUID"</c> on the query.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <returns>a string containing the rendered content</returns>
        private static string Results(HttpContext context)
        {
            // when we're rendering as a button/popup in the corner, we'll pass ?popup=1
            // if it's absent, we're rendering results as a full page for sharing
            var isPopup = !string.IsNullOrWhiteSpace(context.Request["popup"]);

            // this guid is the MiniProfiler.Id property
            // if this guid is not supplied, the last set of results needs to be
            // displayed. The home page doesn't have profiling otherwise.
            Guid id;
            if (!Guid.TryParse(context.Request["id"], out id))
                id = MiniProfiler.Settings.Storage.List(1).FirstOrDefault();

            if (id == default(Guid))
                return isPopup ? NotFound(context) : NotFound(context, "text/plain", "No Guid id specified on the query string");

            MiniProfiler.Settings.EnsureStorageStrategy();
            var profiler = MiniProfiler.Settings.Storage.Load(id);

            var provider = WebRequestProfilerProvider.Settings.UserProvider;
            string user = null;
            if (provider != null)
            {
                user = provider.GetUser(context.Request);
            }

            MiniProfiler.Settings.Storage.SetViewed(user, id);

            if (profiler == null)
            {
                return isPopup ? NotFound(context) : NotFound(context, "text/plain", "No MiniProfiler results found with Id=" + id.ToString());
            }

            bool needsSave = false;
            if (profiler.ClientTimings == null)
            {
                profiler.ClientTimings = ClientTimings.FromRequest(context.Request);
                if (profiler.ClientTimings != null)
                {
                    needsSave = true;
                }
            }

            if (profiler.HasUserViewed == false)
            {
                profiler.HasUserViewed = true;
                needsSave = true;
            }

            if (needsSave) MiniProfiler.Settings.Storage.Save(profiler);

            var authorize = MiniProfiler.Settings.Results_Authorize;

            if (authorize != null && !authorize(context.Request))
            {
                context.Response.ContentType = "application/json";
                return "hidden".ToJson();
            }

            return isPopup ? ResultsJson(context, profiler) : ResultsFullPage(context, profiler);
        }

        /// <summary>
        /// authorize the request. 
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="isList">is list.</param>
        /// <param name="message">The message.</param>
        /// <returns>true if the request is authorised.</returns>
        private static bool AuthorizeRequest(HttpContext context, bool isList, out string message)
        {
            message = null;
            var authorize = MiniProfiler.Settings.Results_Authorize;
            var authorizeList = MiniProfiler.Settings.Results_List_Authorize;

            if ((authorize != null && !authorize(context.Request)) || (isList && (authorizeList == null || !authorizeList(context.Request))))
            {
                context.Response.StatusCode = 401;
                context.Response.ContentType = "text/plain";
                message = "unauthorized";
                return false;
            }

            return true;
        }

        /// <summary>
        /// set the JSON results and the content type.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="profiler">The profiler.</param>
        /// <returns>a string containing the JSON results.</returns>
        private static string ResultsJson(HttpContext context, MiniProfiler profiler)
        {
            context.Response.ContentType = "application/json";
            return MiniProfiler.ToJson(profiler);
        }

        /// <summary>
        /// results full page.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="profiler">The profiler.</param>
        /// <returns>a string containing the results page</returns>
        private static string ResultsFullPage(HttpContext context, MiniProfiler profiler)
        {
            context.Response.ContentType = "text/html";

            var template = GetResource("share.html");
            return template.Format(new
            {
                name = profiler.Name,
                duration = profiler.DurationMilliseconds.ToString(CultureInfo.InvariantCulture),
                path = VirtualPathUtility.ToAbsolute(MiniProfiler.Settings.RouteBasePath).EnsureTrailingSlash(),
                json = MiniProfiler.ToJson(profiler),
                includes = RenderIncludes(profiler),
                version = MiniProfiler.Settings.Version
            });
        }

        /// <summary>
        /// get the resource.
        /// </summary>
        /// <param name="filename">The filename.</param>
        /// <returns>a string containing the resource</returns>
        private static string GetResource(string filename)
        {
            filename = filename.ToLower();
            string result;

#if DEBUG 
            // attempt to simply load from file system, this lets up modify js without needing to recompile A MILLION TIMES 
            if (!bypassLocalLoad)
            {

                var trace = new System.Diagnostics.StackTrace(true);
                var path = System.IO.Path.GetDirectoryName(trace.GetFrames()[0].GetFileName()) + "\\..\\UI\\" + filename;
                try
                {
                    return File.ReadAllText(path);
                }
                catch 
                {
                    bypassLocalLoad = true;
                }
            }
#endif

            if (!ResourceCache.TryGetValue(filename, out result))
            {
                string customTemplatesPath = HttpContext.Current.Server.MapPath(MiniProfiler.Settings.CustomUITemplates);
                string customTemplateFile = Path.Combine(customTemplatesPath, filename);

                if (File.Exists(customTemplateFile))
                {
                    result = File.ReadAllText(customTemplateFile);
                }
                else
                {
                    using (var stream = typeof(MiniProfilerHandler).Assembly.GetManifestResourceStream("StackExchange.Profiling.UI." + filename))
                    using (var reader = new StreamReader(stream))
                    {
                        result = reader.ReadToEnd();
                    }
                }

                ResourceCache[filename] = result;
            }

            return result;
        }

        /// <summary>
        /// Helper method that sets a proper 404 response code.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="contentType">The content Type.</param>
        /// <param name="message">The message.</param>
        /// <returns>a string containing the 'not found' message.</returns>
        private static string NotFound(HttpContext context, string contentType = "text/plain", string message = null)
        {
            context.Response.StatusCode = 404;
            context.Response.ContentType = contentType;

            return message;
        }

    }
}
