using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace grhaWebFunctions.Model
{
    public class hoa_config
    {
        public string? id { get; set; }
        public string? ConfigName { get; set; }
        public string? ConfigDesc { get; set; }
        public string? ConfigValue { get; set; }
        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }

    }
}
