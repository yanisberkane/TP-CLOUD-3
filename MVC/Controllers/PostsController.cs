
using Microsoft.AspNetCore.Mvc;
using MVC.Models;
using MVC.Data;
using MVC.Business;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace MVC.Controllers
{
// [Authorize]
    public class PostsController : Controller
    {
        private IRepository _repo;
        private BlobController _blobController;
        private ServiceBusController _serviceBusController;

        public PostsController(IRepository repo, BlobController blobController, ServiceBusController serviceBusController)
        {
            _repo = repo;
            _blobController = blobController;
            _serviceBusController = serviceBusController;
        }

        // GET: Posts
        public async Task<IActionResult> Index(int pageNumber = 1, int pageSize = 10)
        {
            List<Post> posts = await _repo.GetPostsIndex(pageNumber, pageSize);
            int totalPosts = await _repo.GetPostsCount();
            int totalPages = (int)Math.Ceiling(totalPosts/(double)pageSize);

            PostIndexViewModel viewModel = new PostIndexViewModel { Posts = posts, CurrentPage = pageNumber, TotalPages = totalPages, PageSize = pageSize };

            return View(viewModel);
        }

        // GET: Posts/Create
        public IActionResult Create()
        {
            return View();
        }

        // POST: Posts/Create
        // To protect from overposting attacks, enable the specific properties you want to bind to.
        // For more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Title,Category,User,Created,FileToUpload")] PostForm postForm)
        {

            try
            {
                postForm.BlobImage = Guid.NewGuid();
                postForm.Url = await _blobController.PushImageToBlob(postForm.FileToUpload, (Guid)postForm.BlobImage);

                //retrait de l'erreur du au manque de l'imnage, celle-ci fut ajouter au model de base par notre CopyToAsync.
                ModelState.Remove("BlobImage");
                ModelState.Remove("Url");
            }
            catch (ExceptionFilesize)
            {
                // Fichier trop gros
                // ajout d'une erreur si le fichier est trop gros
                ModelState.AddModelError("FileToUpload", "Le fichier est trop gros.");
            }

            if (ModelState.IsValid)
            {
                await _repo.Add(postForm);

                // Envoie des messages dans le Service Bus
                await _serviceBusController.SendImageToResize((Guid)postForm.BlobImage!, postForm.Id);
                await _serviceBusController.SendContentImageToValidation((Guid)postForm.BlobImage!, Guid.NewGuid(), postForm.Id);

                return RedirectToAction(nameof(Index));
            }
            return View(postForm);
        }

        [HttpPost]
        // Function pour ajouter un like a un Post
        public async Task<ActionResult> Like(Guid id)
        {
            await _repo.IncrementPostLike(id);

            return RedirectToAction("Index");
        }

        [HttpPost]
        // Fonction pour ajouter un dislike a un Post
        public async Task<ActionResult> Dislike(Guid id)
        {
            await _repo.IncrementPostDislike(id);

            return RedirectToAction("Index");
        }
    }
}
