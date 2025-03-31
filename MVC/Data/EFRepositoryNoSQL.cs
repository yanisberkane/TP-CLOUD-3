using Microsoft.EntityFrameworkCore;
using MVC.Models;

namespace MVC.Data
{
    public class EFRepositoryNoSQL : EFRepository<ApplicationDbContextNoSQL>
    {
        public EFRepositoryNoSQL(ApplicationDbContextNoSQL context) : base(context) { }

        public override async Task<List<Post>> GetPostsIndex(int pageNumber, int pageSize)
        {
            // En NoSQL nous ne pouvons pas faire de "include" nous devons donc faire 2 query et les merger.
            List<Post> posts = await _context.Posts.OrderByDescending(o => o.Created).Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync();

            // nous extractons ensuite la list de guid des posts
            List<Guid> postguid = posts.Select(p => p.Id).ToList();

            // nous extractons ensuite les comments relier au posts.
            List<Comment> comments = await _context.Comments.Where(c => postguid.Contains(c.PostId)).ToListAsync();

            // agregation du lots
            foreach (var post in posts)
            {
                post.Comments = comments.Where(w => w.PostId == post.Id).ToList();
            }

            return posts;
        }
    }
}
