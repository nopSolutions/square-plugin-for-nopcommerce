using System.Web.Mvc;
using System.Web.Routing;
using Nop.Web.Framework.Mvc.Routes;

namespace Nop.Plugin.Payments.Square.Infrastructure
{
    public partial class RouteProvider : IRouteProvider
    {
        /// <summary>
        /// Register routes
        /// </summary>
        /// <param name="routes">Routes</param>
        public void RegisterRoutes(RouteCollection routes)
        {
            //add route for the access token callback
            routes.MapRoute(SquarePaymentDefaults.AccessTokenRoute,
                 "Plugins/PaymentSquare/AccessToken/", new { controller = "PaymentSquare", action = "AccessTokenCallback" },
                 new[] { "Nop.Plugin.Payments.Square.Controllers" });
        }

        /// <summary>
        /// Gets a priority of route provider
        /// </summary>
        public int Priority
        {
            get { return 0; }
        }
    }
}