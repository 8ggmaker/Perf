using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Common.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ZipStreamWeb
{
   public class Startup
   {
      public Startup( IConfiguration configuration )
      {
         Configuration = configuration;
      }

      public IConfiguration Configuration { get; }

      // This method gets called by the runtime. Use this method to add services to the container.
      public void ConfigureServices( IServiceCollection services )
      {
         ThreadPool.SetMinThreads( 128, 128 );
         ServicePointManager.DefaultConnectionLimit = 128;

         string azureStorageConnectionString = Configuration[ "AzureStorageConnectionString" ];
         AzureBlobHelper azureBlobHelper = new AzureBlobHelper( azureStorageConnectionString );
         services.AddSingleton<AzureBlobHelper>(azureBlobHelper);
         services.AddControllers();
      }

      // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
      public void Configure( IApplicationBuilder app, IWebHostEnvironment env )
      {
         if( env.IsDevelopment() )
         {
            app.UseDeveloperExceptionPage();
         }

         app.UseHttpsRedirection();

         app.UseRouting();

         app.UseAuthorization();

         app.UseEndpoints( endpoints =>
          {
             endpoints.MapControllers();
          } );
      }
   }
}
