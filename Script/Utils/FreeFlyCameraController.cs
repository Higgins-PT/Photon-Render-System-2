using UnityEngine;

namespace PhotonSystem
{
    [RequireComponent(typeof(Camera))]
    public class FreeFlyCameraController : MonoBehaviour
    {
        [Header("Movement Settings")]
        [SerializeField] private float moveSpeed = 5f;
        [SerializeField] private float fastMoveMultiplier = 4f;
        [SerializeField] private float positionSmoothTime = 0.05f;

        [Header("Mouse Settings")]
        [SerializeField] private float mouseSensitivity = 0.2f;

        private Camera _camera;
        private bool _isRightMouseHeld;
        private Vector3 _currentEuler;

        private Vector3 _targetPosition;
        private Vector3 _positionVelocity;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            _currentEuler = transform.rotation.eulerAngles;
            _targetPosition = transform.position;
        }

        private void Update()
        {
            HandleMouseInput();
            HandleMovement();
        }

        private void HandleMouseInput()
        {
            if (Input.GetMouseButtonDown(1))
            {
                _isRightMouseHeld = true;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            else if (Input.GetMouseButtonUp(1))
            {
                _isRightMouseHeld = false;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            if (!_isRightMouseHeld)
                return;

            float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity;
            float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity;

            _currentEuler.x -= mouseY;
            _currentEuler.y += mouseX;
            _currentEuler.x = Mathf.Clamp(_currentEuler.x, -89f, 89f);

            transform.rotation = Quaternion.Euler(_currentEuler);
        }

        private void HandleMovement()
        {
            Vector3 move = Vector3.zero;
            move += transform.forward * Input.GetAxisRaw("Vertical");   // W/S
            move += transform.right * Input.GetAxisRaw("Horizontal");   // A/D
            if (Input.GetKey(KeyCode.Q))
                move += transform.up;
            if (Input.GetKey(KeyCode.E))
                move += -transform.up;

            if (move.sqrMagnitude > Mathf.Epsilon)
            {
                move.Normalize();

                float speed = moveSpeed;
                if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                    speed *= fastMoveMultiplier;

                _targetPosition += move * speed * Time.deltaTime;
            }

            transform.position = Vector3.SmoothDamp(
                transform.position,
                _targetPosition,
                ref _positionVelocity,
                positionSmoothTime);
        }
    }
}

