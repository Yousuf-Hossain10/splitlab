using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Splitlab.Api;

/// <summary>
/// Publishes the API's OpenAPI document at <c>/swagger/v1/swagger.json</c>. The dashboard's TypeScript client is generated
/// from it (ADR-0008), and a test fails if the committed copy is out of date.
/// </summary>
public static class OpenApiSetup
{
    public const string DocumentPath = "/swagger/v1/swagger.json";

    public static IServiceCollection AddSplitlabOpenApi(this IServiceCollection services)
    {
        // Swashbuckle describes enums using MVC's JSON options, while minimal APIs serialize with the HTTP JSON options.
        // Both must agree (enums as their names), or the document would say numbers while the API sends strings.
        services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(o =>
            o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Splitlab API",
                Version = "v1",
                Description = "Feature flags and A/B experimentation. People use bearer JWTs; SDKs use environment-scoped keys (sl_...).",
            });
            options.SupportNonNullableReferenceTypes();
            options.SchemaFilter<RequireNonNullablePropertiesFilter>();
            options.AddSecurityDefinition("bearer", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                Description = "A person's JWT, or an SDK key for the config and events endpoints.",
            });
            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "bearer" } }] = [],
            });
        });
        return services;
    }

    /// <summary>
    /// Makes the document say what the API really does. Properties that are not nullable become required, so generated
    /// clients type guaranteed fields as present (Swashbuckle only does this for <c>[Required]</c>). Nullable properties that
    /// refer to another schema are wrapped so they are described as nullable (OpenAPI 3.0 ignores <c>nullable</c> beside a plain reference).
    /// </summary>
    private sealed class RequireNonNullablePropertiesFilter : ISchemaFilter
    {
        public void Apply(OpenApiSchema schema, SchemaFilterContext context)
        {
            if (schema.Type != "object" || schema.Properties is not { Count: > 0 })
            {
                return;
            }

            var nullability = new System.Reflection.NullabilityInfoContext();
            foreach (string name in schema.Properties.Keys.ToList())
            {
                OpenApiSchema property = schema.Properties[name];
                System.Reflection.PropertyInfo? clr = context.Type.GetProperties()
                    .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                bool nullable = property.Nullable
                    || (clr is not null && nullability.Create(clr).ReadState == System.Reflection.NullabilityState.Nullable);

                if (nullable && property.Reference is not null)
                {
                    schema.Properties[name] = new OpenApiSchema { Nullable = true, AllOf = [property] };
                }

                if (!nullable && !property.ReadOnly)
                {
                    schema.Required.Add(name);
                }
            }
        }
    }
}
