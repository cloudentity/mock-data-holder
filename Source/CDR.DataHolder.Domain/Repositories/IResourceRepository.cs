using System;
using System.Threading.Tasks;
using CDR.DataHolder.Domain.Entities;
using CDR.DataHolder.Domain.ValueObjects;

namespace CDR.DataHolder.Domain.Repositories
{
	public interface IResourceRepository
	{
		Task<Customer> GetCustomer(String customerId);
		Task<Customer> GetCustomerByLoginId(string loginId);
		Task<bool> CanAccessAccount(string accountId, String customerId);
		Task<Page<Account[]>> GetAllAccounts(AccountFilter filer, int page, int pageSize);
		Task<Account[]> GetAllAccountsByCustomerIdForConsent(String customerId);
		Task<Page<AccountTransaction[]>> GetAccountTransactions(AccountTransactionsFilter transactionsFilter, int page, int pageSize);
	}
}
