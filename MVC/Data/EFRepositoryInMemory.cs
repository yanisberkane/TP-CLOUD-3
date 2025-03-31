using Microsoft.EntityFrameworkCore;
using MVC.Models;

namespace MVC.Data
{
    public class EFRepositoryInMemory : EFRepository<ApplicationDbContextInMemory>
    {
        public EFRepositoryInMemory(ApplicationDbContextInMemory context) : base(context) { }

        // Le même que SQL, ceci pourrait être bouger dans la base class ...
        public override async Task<List<Post>> GetPostsIndex(int pageNumber, int pageSize) { return await _context.Posts.OrderByDescending(o => o.Created).Skip((pageNumber - 1) * pageSize).Take(pageSize).Include(i => i.Comments).ToListAsync(); }
    }
}
