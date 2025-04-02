
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MVC.Business;
using MVC.Data;
using MVC.Models;

namespace MVC.Controllers
{
    [Authorize]
    public class CommentsController : Controller
    {
        private IRepository _repo;
        private ServiceBusController _serviceBusController;

        public CommentsController(IRepository repo, ServiceBusController serviceBusController)
        {
            _repo = repo;
            _serviceBusController = serviceBusController;
        }

        // GET: Comments
        public async Task<IActionResult> Index(Guid id)
        {
            return View(await _repo.GetCommentsIndex(id));
        }


        // Received Post ID
        // GET: Comments/Create/{PostId}{CommentId}
        // int PostId
        [HttpGet]
        public IActionResult Create(Guid PostId)
        {
            ViewData["PostId"] = PostId;
            return View();
        }

        // POST: Comments/Create
        // To protect from overposting attacks, enable the specific properties you want to bind to.
        // For more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Commentaire,User,PostId")] Comment comment)
        {
            // Retrait de l'erreur dans le modèle pour le manque de l'object de navigation
            ModelState.Remove("Post");

            if (ModelState.IsValid)
            {
                await _repo.AddComments(comment);

                await _serviceBusController.SendContentTextToValidation(comment.Commentaire, comment.Id, comment.PostId);

                // la fonction Index s'attend a un object nommé id ...
                return RedirectToAction(nameof(Index), new { id = comment.PostId } );
            }

            // la fonction Index s'attend a un object nommé id ...
            return RedirectToAction(nameof(Index), new { id = comment.PostId } );
        }

        [HttpPost]
        // Function pour ajouter un like a un Comment
        public async Task<ActionResult> Like(Guid CommentId, Guid PostId)
        {
            await _repo.IncrementCommentLike(CommentId);

            // la fonction Index s'attend a un object nommé id ...
            return RedirectToAction(nameof(Index), new { id = PostId });
        }

        [HttpPost]
        // Fonction pour ajouter un dislike a un Comment
        public async Task<ActionResult> Dislike(Guid CommentId, Guid PostId)
        {
            await _repo.IncrementCommentDislike(CommentId);

            // la fonction Index s'attend a un object nommé id ...
            return RedirectToAction(nameof(Index), new { id = PostId });
        }

    }
}
