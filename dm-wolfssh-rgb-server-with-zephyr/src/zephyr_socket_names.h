/* Maps send()/recv() to zsock_* for wolfSSH's io.c; force-included by CMakeLists.txt. */

#ifndef APP_ZEPHYR_SOCKET_NAMES_H_
#define APP_ZEPHYR_SOCKET_NAMES_H_

#ifndef send
#define send zsock_send
#endif

#ifndef recv
#define recv zsock_recv
#endif

#endif /* APP_ZEPHYR_SOCKET_NAMES_H_ */
