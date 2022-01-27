using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using CDR.DataHolder.API.Infrastructure.IdPermanence;
using Microsoft.AspNetCore.Authentication;

namespace CDR.DataHolder.Resource.API.Business.Services
{
	public class TokenTransformService : IClaimsTransformation
	{
		private readonly IIdPermanenceManager _idPermanenceManager;

		public TokenTransformService(IIdPermanenceManager idPermanenceManager)
		{
			_idPermanenceManager = idPermanenceManager;
		}

		public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
		{
			// Clone current identity
			var clone = principal.Clone();
			var newIdentity = (ClaimsIdentity)clone.Identity;

			var subClaim = newIdentity.FindFirst(c => c.Type == ClaimTypes.NameIdentifier);
			if (subClaim == null)
			{
				return Task.FromResult(principal);
			}

			return Task.FromResult(clone);
		}
	}
}
