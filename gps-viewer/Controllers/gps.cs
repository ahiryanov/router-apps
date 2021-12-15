using Microsoft.AspNetCore.Mvc;

namespace gps_viewer.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class gps : Controller
    {
        [HttpGet]
        public string Get()
        {
            var gps = GetGps.current;
            if (gps != null)
                return $"{gps.Lat} {gps.Lon} {gps.Speed}";
            else
                return "Not found";
        }

        [HttpGet]
        [Route("json")]

        public JsonResult GetJson()
        {
            var gps = GetGps.current;
            if (gps != null)
                return Json(gps);
            else
                return Json("Not found");
        }
    }
}
